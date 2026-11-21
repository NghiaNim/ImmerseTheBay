// Real portal decisions; --live also exercises real Dynamic reset/reseed/playback.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs');
const assert = require('node:assert/strict');
const live = process.argv.includes('--live');
const base = process.env.PORTAL_URL || 'http://localhost:8793';
const out = process.env.E2E_ARTIFACTS || '/tmp/il-preset-e2e';
fs.mkdirSync(out, { recursive: true });
(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH, args: ['--autoplay-policy=no-user-gesture-required'] });
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.setDefaultTimeout(60000);
  const errors = [], turns = [], reads = [], frames = [], transitions = [], controlEvents = [];
  let firstSession; const started = Date.now();
  const deadline = setTimeout(async () => { try { if (!page.isClosed()) await page.evaluate(() => document.querySelector('#leave').onclick()); } finally { await browser.close(); } }, 180000);
  page.on('pageerror', e => errors.push(e.message));
  page.on('websocket', ws => ws.on('framereceived', ({payload}) => { try { const m = JSON.parse(String(payload)); if (['chunk_complete','generation_started','generation_reset','command_error'].includes(m.type)) controlEvents.push({at:Date.now(),...m}); } catch {} }));
  page.on('response', r => { if (r.url().endsWith('/api/agent/turn') && r.ok()) reads.push(r.json().then(t => turns.push(t))); });
  await page.addInitScript(() => { window.sceneTestTrace=[];const seen=new Set();new MutationObserver(()=>{try{const trace=JSON.parse(document.querySelector('#runtimeTrace')?.textContent||'{}');for(const e of trace.events||[]){const key=JSON.stringify(e);if(!seen.has(key)){seen.add(key);window.sceneTestTrace.push(e);}}}catch{}}).observe(document,{subtree:true,childList:true,characterData:true}); });
  const decision = () => page.locator('#decision').textContent().then(JSON.parse);
  const ready = async () => {
    await page.waitForFunction(() => !document.querySelector('#pinch').disabled || document.querySelector('#notice').classList.contains('error'), {}, { timeout: 90000 });
    assert.equal(await page.locator('#pinch').isDisabled(), false, await page.locator('#notice').innerText());
  };
  const capture = async name => {
    if (live) {
      await page.waitForFunction(() => { const v = document.querySelector('#video'); return v.videoWidth > 0 && !v.paused; });
      await page.waitForTimeout(700);
      const f = await page.locator('#video').evaluate(v => {
        const c = document.createElement('canvas'); c.width = v.videoWidth; c.height = v.videoHeight; c.getContext('2d').drawImage(v, 0, 0);
        return { width: v.videoWidth, height: v.videoHeight, time: v.currentTime, png: c.toDataURL('image/png').split(',')[1] };
      });
      fs.writeFileSync(`${out}/${name}.png`, Buffer.from(f.png, 'base64')); delete f.png; frames.push({ name, ...f });
    }
    await page.screenshot({ path: `${out}/${live ? 'live' : 'preview'}-${name}.png` });
  };
  const switchScene = async (button, id) => {
    await page.locator(button).click(); await ready();
    const d = await decision(); assert.equal(d.response.scene.id, id); assert.equal(d.request.sessionId, firstSession);
    assert.equal(await page.locator('#sceneTransition').evaluate(el => el.classList.contains('visible')), false);
    if (live) {
      const names = d.execution.visko.map(c => c.name);
      assert.equal(names.filter(n => n === 'reset').length, 1); assert.equal(names.filter(n => n === 'set_image').length, 1); assert.equal(names.filter(n => n === 'start').length, 1);
      assert.ok(names.indexOf('reset') < names.indexOf('set_image')); assert.ok(names.indexOf('set_image') < names.indexOf('set_prompt')); assert.ok(names.indexOf('set_prompt') < names.indexOf('start'));
      const trace = JSON.parse(await page.locator('#runtimeTrace').textContent());
      const event = trace.events.find(e => e.kind === 'scene_transition_video_ready' && e.sceneId === id);
      assert.ok(event, 'New generation/frame evidence required'); transitions.push(event);
    }
    await capture(id); return d.response;
  };
  try {
    await page.goto(`${base}/learn.html`); await page.waitForFunction(() => !document.querySelector('#begin').disabled);
    await page.locator('#lesson').selectOption('ja-konbini-001');
    await page.waitForFunction(() => !document.querySelector('#begin').disabled);
    await page.locator('#previewOnly').setChecked(!live); await page.locator('#begin').click(); await ready();
    const first = await decision(); firstSession = first.request.sessionId; assert.equal(first.response.scene.id, 'checkout'); await capture('opening');
    await switchScene('#presetSandwiches', 'sandwiches');
    await page.locator('#pinch').click(); await ready();
    const small = await decision(); assert.equal(small.response.scene.id, 'sandwiches'); assert.ok(!small.execution.visko.some(c => ['reset', 'set_image', 'start'].includes(c.name)), 'Same-scene step must keep running video');
    await switchScene('#presetDrinks', 'drinks');
    const returned = await switchScene('#returnMain', 'checkout'); assert.equal(returned.route.kind, 'rejoin'); assert.equal(returned.overlay.jp, first.response.overlay.jp);
    await Promise.all(reads); assert.equal(turns.length, 5); assert.deepEqual(turns.map(t => t.operation.revision), [1,2,3,4,5]);
    assert.ok(turns.every(t => t.operation.runId === first.response.operation.runId)); assert.deepEqual(errors, []);
    const report = { live, elapsedMs: Date.now() - started, scenes: turns.map(t => t.scene.id), revisions: turns.map(t => t.operation.revision), frames, transitions, errors };
    fs.writeFileSync(`${out}/${live ? 'live' : 'preview'}-report.json`, JSON.stringify(report, null, 2)); console.log(JSON.stringify(report));
  } catch (error) {
    if (!page.isClosed()) { await page.screenshot({ path: `${out}/failure.png` }); fs.writeFileSync(`${out}/failure-trace.json`, await page.locator('#runtimeTrace').textContent()); console.error(await page.locator('#notice').innerText()); }
    throw error;
  } finally {
    fs.writeFileSync(`${out}/control-events.json`, JSON.stringify(controlEvents,null,2));
    if (!page.isClosed()) { fs.writeFileSync(`${out}/runtime-events.json`,JSON.stringify(await page.evaluate(()=>window.sceneTestTrace),null,2)); await page.evaluate(() => document.querySelector('#leave').onclick()); assert.equal(await page.locator('#leave').isDisabled(), true); }
    clearTimeout(deadline); await browser.close();
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
