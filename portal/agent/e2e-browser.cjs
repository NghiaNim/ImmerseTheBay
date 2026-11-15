// Run with PLAYWRIGHT_MODULE pointing to an existing Playwright installation.
// Speech callback is simulated; lesson API and configured model are real.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const live = process.argv.includes('--live');
const speech = process.argv.includes('--speech');
const dropResponse = process.argv.includes('--drop-response');
const base = process.env.PORTAL_URL || 'http://localhost:8792';
const artifacts = process.env.E2E_ARTIFACTS || '/tmp/il-context-e2e';
fs.mkdirSync(artifacts, { recursive: true });
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.CHROME_PATH ? { executablePath: process.env.CHROME_PATH } : {}), args: process.argv.includes('--strict-autoplay') ? [] : ['--autoplay-policy=no-user-gesture-required'] });
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.setDefaultTimeout(15000);
  const errors = [], turns = [], speechResponses = [], nativeFrames = [], droppedOperations = [], started = Date.now();
  const deadline = setTimeout(() => void page.locator('#leave').click({ timeout: 2000 }).finally(() => browser.close()), 110000);
  page.on('pageerror', (error) => errors.push(error.message));
  page.on('response', async (response) => {
    if (response.url().endsWith('/api/agent/speak')) speechResponses.push(response.status());
    if (response.url().endsWith('/api/agent/turn') && response.ok()) turns.push(await response.json());
  });
  await page.addInitScript(() => {
    window.SpeechRecognition = class {
      start() { this.onstart?.(); setTimeout(() => { this.onresult?.({ resultIndex: 0, results: [Object.assign([{ transcript: 'I want a sandwich, then return to the lesson' }], { isFinal: true })] }); this.onend?.(); }, 10); }
      stop() { this.onend?.(); }
      abort() { this.onend?.(); }
    };
  });
  if (dropResponse) await page.route('**/api/agent/turn', async (route) => {
    const request = route.request().postDataJSON();
    if (!droppedOperations.length && request.context?.gesture === 'pinch') {
      const response = await route.fetch(); assert.equal(response.status(), 200);
      droppedOperations.push(request.operation.requestId);
      await route.abort('failed'); // Server committed; client never receives this response.
    } else await route.continue();
  });
  const ready = async () => {
    await page.waitForFunction(() => !document.querySelector('#pinch').disabled || document.querySelector('#notice').classList.contains('error'), {}, { timeout: 90000 });
    assert.equal(await page.locator('#pinch').isDisabled(), false, await page.locator('#notice').innerText());
  };
  const decision = () => page.locator('#decision').textContent().then(JSON.parse).then((value) => value.response);
  const capture = async (name) => {
    if (live) {
      await page.waitForFunction(() => { const v = document.querySelector('#video'); return v.videoWidth > 0 && v.currentTime > 0 && !v.paused; }, {}, { timeout: 20000 });
      const before = await page.locator('#video').evaluate((v) => v.currentTime);
      await page.waitForTimeout(7000);
      assert.ok(await page.locator('#video').evaluate((v) => v.currentTime) > before, 'Video must advance');
    }
    if (live) {
      const frame = await page.locator('#video').evaluate((v) => {
        const canvas = document.createElement('canvas'); canvas.width = v.videoWidth; canvas.height = v.videoHeight;
        canvas.getContext('2d').drawImage(v, 0, 0);
        return { width: v.videoWidth, height: v.videoHeight, mediaTime: v.currentTime, png: canvas.toDataURL('image/png').split(',')[1] };
      });
      fs.writeFileSync(path.join(artifacts, `native-${name}.png`), Buffer.from(frame.png, 'base64'));
      nativeFrames.push({ name, width: frame.width, height: frame.height, mediaTime: frame.mediaTime });
    }
    await page.screenshot({ path: path.join(artifacts, `${live ? 'live' : 'practice'}-${name}.png`) });
  };
  try {
    await page.goto(`${base}/learn.html`);
    await page.waitForFunction(() => !document.querySelector('#begin').disabled);
    await page.getByText('Guide language & session options', { exact: true }).click();
    await page.locator('#speechEngine').selectOption('browser');
    await page.locator('#previewOnly').setChecked(!live);
    await page.locator('#begin').click(); await ready();
    const first = await decision();
    assert.equal(await page.locator('#romaji').isVisible(), true);
    assert.equal(await page.locator('#romaji').textContent(), first.overlay.romaji);
    assert.equal(await page.locator('#translations').isVisible(), false);
    if (speech) {
      assert.equal(first.speech.role, 'clerk'); assert.notEqual(first.speech.text, first.overlay.jp);
      await page.waitForFunction(() => document.querySelector('#ttsStatus').textContent.includes('speaking'), {}, { timeout: 20000 });
      await page.waitForFunction(() => document.querySelector('#ttsStatus').textContent === '', {}, { timeout: 20000 });
      const originalVolume = await page.locator('#video').evaluate((v) => v.volume);
      await page.locator('#listen').click();
      await page.waitForFunction(() => document.querySelector('#ttsStatus').textContent.includes('speaking'), {}, { timeout: 20000 });
      assert.ok(await page.locator('#video').evaluate((v) => v.volume) < originalVolume);
      await page.waitForFunction(() => document.querySelector('#ttsStatus').textContent === '', {}, { timeout: 20000 });
      assert.equal(await page.locator('#video').evaluate((v) => v.volume), originalVolume);
      assert.match(first.visko.find((c) => c.name === 'set_audio_prompt').data.prompt, /Environmental sounds only; no speech/);
    }
    await capture('opening');
    await page.locator('#speechMode').selectOption('intent');
    await page.locator('#speak').click();
    await page.waitForFunction(() => document.querySelector('#routeStatus').textContent.includes('1/3'), {}, { timeout: 20000 });
    await ready(); assert.equal((await decision()).route.kind, 'detour');
    await capture('detour');
    for (let i = 0; i < 3; i++) { await page.locator('#pinch').click(); await ready(); if (i < 2 && live) await capture(`detour-${i + 2}`); }
    const rejoin = await decision();
    assert.equal(rejoin.route.kind, 'rejoin'); assert.equal(rejoin.overlay.jp, first.overlay.jp);
    await capture('rejoin');
    assert.equal(turns.length, 5);
    assert.deepEqual(turns.map((turn) => turn.operation.revision), [1, 2, 3, 4, 5]);
    if (dropResponse) { assert.equal(droppedOperations.length, 1); assert.equal(turns[2].operation.replayed, true); assert.equal(turns[2].operation.requestId, droppedOperations[0]); }
    for (const turn of turns) for (const command of turn.visko.filter((c) => c.name === 'set_prompt')) {
      assert.match(command.data.prompt, /established face, hair and clothing remain unchanged/);
      assert.ok(command.data.prompt.length <= 600);
      assert.ok(command.data.prompt.split(/\s+/u).length <= 95);
    }
    assert.deepEqual(errors, []);
    if (speech) { assert.ok(speechResponses.length >= 2); assert.ok(speechResponses.every((status) => status === 200)); }
    const report = { mode: live ? 'real-dynamic' : 'practice-real-api', speech: 'simulated browser recognition callback', cartesia: speech ? { realPlayback: true, responses: speechResponses } : 'not asserted', turns: turns.length, routes: turns.map((t) => t.route.kind), elapsedMs: Date.now() - started, visualIdentity: 'requires human frame review', nativeFrames, droppedOperations, operationRevisions: turns.map((t) => t.operation.revision), prompts: turns.map((t) => t.visko.find((c) => c.name === 'set_prompt')?.data.prompt) };
    fs.writeFileSync(path.join(artifacts, `${live ? 'live' : 'practice'}-report.json`), JSON.stringify(report, null, 2));
    console.log(JSON.stringify(report));
  } catch (error) {
    await page.screenshot({ path: path.join(artifacts, `${live ? 'live' : 'practice'}-failure.png`) }).catch(() => {});
    console.error(await page.locator('#notice').innerText().catch(() => 'Page unavailable'));
    throw error;
  } finally {
    try {
      if (await page.locator('#leave').isEnabled()) await page.evaluate(() => document.querySelector('#leave').onclick());
      assert.doesNotMatch(await page.locator('#notice').innerText(), /Disconnect did not confirm/);
      assert.equal(await page.locator('#leave').isDisabled(), true);
      console.log('Leave handler and disconnect completed');
    }
    finally { clearTimeout(deadline); await browser.close(); }
  }
})().catch((error) => { console.error(error.message); process.exitCode = 1; });
