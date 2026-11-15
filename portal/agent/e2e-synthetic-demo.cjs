// Automated browser demonstration using synthetic audio, real recognition and real scoring.
// Never opens the computer microphone or a billed Dynamic video session.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const origin = process.env.PORTAL_URL || 'http://localhost:8793';
const out = path.resolve(process.env.E2E_ARTIFACTS || '/tmp/itb-synthetic-demo');
const lessonId = 'ja-konbini-demo-001';
const cases = [
  { id: 'sandwich-pass', text: 'サンドイッチはどこですか。', scene: 'sandwiches', outcome: 'pass' },
  { id: 'water-retry', text: 'おみずがください。', scene: 'drinks', outcome: 'retry', focus: 'を' },
  { id: 'water-assisted', text: 'お水をください。', scene: 'drinks', outcome: 'assisted', hint: true },
  { id: 'checkout-pass', text: 'カードで払えますか。', scene: 'checkout', outcome: 'pass' },
];
const headings = {
  pass: 'Good job',
  retry: 'Please try again',
  assisted: 'Matched with a hint',
};
const dimensions = { width: 1440, height: 900 };
const format = 'h264';
fs.mkdirSync(out, { recursive: true });

function ffmpeg(args) {
  execFileSync(process.env.FFMPEG || 'ffmpeg', ['-hide_banner', '-loglevel', 'error', '-y', ...args],
    { cwd: out, stdio: 'inherit', timeout: 45_000 });
}

function mp4(shots) {
  const segments = shots.map(({ id, audio, seconds }, index) => {
    const output = `segment-${index}.mp4`;
    const source = audio ? ['-i', audio] : ['-f', 'lavfi', '-i', 'anullsrc=r=48000:cl=stereo'];
    ffmpeg(['-loop', '1', '-framerate', '30', '-i', `${id}.png`, ...source,
      '-map', '0:v:0', '-map', '1:a:0', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-tune', 'stillimage',
      '-c:a', 'aac', '-ar', '48000', '-ac', '2', '-af', 'apad', '-t', String(seconds), output]);
    return output;
  });
  fs.writeFileSync(path.join(out, 'segments.txt'), segments.map(name => `file '${name}'\n`).join(''));
  ffmpeg(['-f', 'concat', '-safe', '0', '-i', 'segments.txt', '-c', 'copy', '-movflags', '+faststart', 'tenin-synthetic-demo.mp4']);
  for (const name of [...segments, 'segments.txt']) fs.unlinkSync(path.join(out, name));
  const info = JSON.parse(execFileSync(process.env.FFPROBE || 'ffprobe',
    ['-v', 'error', '-show_streams', '-show_format', '-of', 'json', path.join(out, 'tenin-synthetic-demo.mp4')],
    { encoding: 'utf8', timeout: 10_000 }));
  assert.equal(info.streams.find(stream => stream.codec_type === 'video')?.codec_name, format);
  assert.ok(info.streams.some(stream => stream.codec_type === 'audio'));
  return Number(info.format.duration);
}

async function installSyntheticMicrophone(page) {
  await page.addInitScript(() => {
    let context;
    let destination;
    navigator.mediaDevices.getUserMedia = async () => {
      if (context) await context.close();
      context = new AudioContext({ sampleRate: 44100 });
      await context.resume();
      destination = context.createMediaStreamDestination();
      return destination.stream;
    };
    window.feedDemoSpeech = async (base64) => {
      const bytes = Uint8Array.from(atob(base64), ch => ch.charCodeAt(0));
      const buffer = await context.decodeAudioData(bytes.buffer);
      const source = context.createBufferSource();
      source.buffer = buffer;
      source.connect(destination);
      source.start(context.currentTime + 0.16);
      await new Promise(resolve => { source.onended = resolve; });
      return buffer.duration;
    };
  });
}

async function shot(page, id, { scene, headline, text = '', score, correction, audio, seconds = 2 }, shots) {
  await page.evaluate(async ({ scene, headline, text, score, correction }) => {
    if (scene) {
      // Test-only scene fixture preview. The decision's authenticated asset is real.
      const images = ['photoPreview', 'sceneFocus'].map(id => document.getElementById(id));
      const asset = new URL(scene.startImage, location.origin);
      const token = new URLSearchParams(location.search).get('portal_token');
      if (token) asset.searchParams.set('portal_token', token);
      for (const image of images) {
        image.src = asset.href;
        await image.decode();
        image.hidden = false;
      }
      document.getElementById('scenePlaceholder').hidden = true;
      document.getElementById('sceneStatus').textContent = 'Scene image • ' + scene.label;
    }
    let evidence = document.getElementById('automated-rehearsal-evidence');
    if (!evidence) {
      evidence = document.createElement('aside');
      evidence.id = 'automated-rehearsal-evidence';
      evidence.style.cssText = 'position:fixed;z-index:1000;right:30px;top:78px;max-width:420px;padding:18px 22px;background:#13221f;color:#fff;border:1px solid #8cae9e;border-radius:14px;font:600 20px system-ui;box-shadow:0 8px 24px #0006;white-space:pre-line;pointer-events:none';
      document.body.append(evidence);
    }
    evidence.textContent = [
      'TENIN · AUTOMATED BROWSER REHEARSAL',
      headline, score === undefined ? '' : `Sentence match ${score}%`, text,
      correction ? `Practice 「${correction}」 again` : '',
    ].filter(Boolean).join('\n');
  }, { scene, headline, text, score, correction });
  await page.screenshot({ path: path.join(out, `${id}.png`), animations: 'disabled' });
  shots.push({ id, audio, seconds });
}

async function main() {
  const browser = await chromium.launch({
    headless: true,
    executablePath: process.env.CHROME_PATH,
    args: ['--autoplay-policy=no-user-gesture-required'],
  });
  try {
    const page = await browser.newPage({ viewport: dimensions });
    page.setDefaultTimeout(20_000);
    await installSyntheticMicrophone(page);
    const url = new URL('/learn.html', origin);
    const auth = process.env.PORTAL_TOKEN ? { portal_token: process.env.PORTAL_TOKEN } : {};
    if (process.env.PORTAL_TOKEN) url.searchParams.set('portal_token', process.env.PORTAL_TOKEN);
    const api = (route, data) => page.request.post(new URL(route, origin).href, { data, headers: auth });
    const currentTurn = async () => JSON.parse(await page.locator('#decision').textContent());
    const waitForTurn = async (revision) => {
      await page.waitForFunction(expected => {
        try { return JSON.parse(document.getElementById('decision').textContent).response.operation.revision >= expected; }
        catch { return false; }
      }, revision);
      const result = await currentTurn();
      assert.equal(result.response.operation.revision, revision);
      return result;
    };
    const shots = [];
    const evidence = [];
    await page.goto(url.href);
    await page.waitForFunction(id => [...document.getElementById('lesson').options].some(option => option.value === id), lessonId);
    await page.locator('#lesson').selectOption(lessonId);
    await page.waitForFunction(() => document.getElementById('lessonInfo').textContent.includes('3 spoken lines'));
    await page.locator('#previewOnly').setChecked(true);
    await page.locator('#begin').click();
    let revision = 1;
    let active = await waitForTurn(revision);
    await page.locator('#translationToggle').click();

    await shot(page, 'opening', { scene: active.response.scene, headline: 'Three-scene Japanese practice',
      text: 'Synthetic learner audio · real transcription and scoring\nCurated scene images · browser preview', seconds: 3 }, shots);
    for (const sample of cases) {
      assert.equal(active.response.scene.id, sample.scene);
      const target = active.response.overlay.jp;
      if (sample.hint) {
        await page.locator('#longPinch').click();
        active = await waitForTurn(++revision);
        await shot(page, `${sample.id}-hint`, { scene: active.response.scene, headline: 'Hint requested', text: target }, shots);
      }
      const tts = await api('/api/agent/speak', { text: sample.text, language: 'ja' });
      assert.equal(tts.status(), 200, `TTS unavailable for ${sample.id}`);
      const wav = await tts.body();
      const audio = `${sample.id}.wav`;
      fs.writeFileSync(path.join(out, audio), wav);
      const heardResponse = page.waitForResponse(r => r.url().endsWith('/api/agent/transcribe'));
      await page.locator('#speak').click();
      await page.locator('#stopSpeech').waitFor({ state: 'visible' });
      const before = `${sample.id}-speaking`;
      const duration = await page.evaluate(base64 => window.feedDemoSpeech(base64), wav.toString('base64'));
      await shot(page, before, { scene: active.response.scene, headline: 'Synthetic learner is speaking',
        text: target, audio, seconds: Math.max(1.7, duration + 0.55) }, shots);
      await page.locator('#stopSpeech').click();
      const heard = await heardResponse;
      assert.equal(heard.status(), 200, `STT unavailable for ${sample.id}`);
      const { text: transcript } = await heard.json();
      active = await waitForTurn(++revision);
      const attempt = active.response.attempt;
      assert.equal(attempt?.outcome, sample.outcome, `${sample.id}: heard «${transcript}», received ${attempt?.outcome}`);
      if (sample.focus) assert.equal(attempt.correction?.jp, sample.focus);
      evidence.push({ target, scene: sample.scene, syntheticSpeech: sample.text,
        transcript, score: attempt.score, outcome: attempt.outcome, correction: attempt.correction?.jp || null });
      await shot(page, `${sample.id}-result`, { scene: active.response.scene, headline: headings[attempt.outcome],
        text: `Recognized: ${transcript}`, score: attempt.score, correction: attempt.correction?.jp }, shots);
    }
    await page.locator('#practiceReview').waitFor({ state: 'visible' });
    await shot(page, 'summary', { headline: 'Three scenes complete', text: 'Two independent passes · one hint-assisted pass', seconds: 4 }, shots);
    const binding = { sessionId: active.request.sessionId, lessonId };
    const response = await api('/api/agent/summary', binding);
    assert.equal(response.status(), 200, 'Completed practice summary unavailable');
    const summary = await response.json();
    assert.equal(summary.runId, active.response.operation.runId);
    assert.equal(summary.targetsCompleted, 3);
    const report = { mode: 'browser practice preview with real TTS and STT', grading: 'recognized sentence match', lessonId, evidence, summary };
    fs.writeFileSync(path.join(out, 'report.json'), JSON.stringify({ ...report, durationSeconds: mp4(shots) }, null, 2));
    console.log(evidence.map(({ outcome, score, transcript }) => `${outcome} ${score}% · ${transcript}`).join('\n'));
    console.log(path.join(out, 'tenin-synthetic-demo.mp4'));
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  const token = process.env.PORTAL_TOKEN;
  const message = token ? error.message.replaceAll(token, '[redacted]').replaceAll(encodeURIComponent(token), '[redacted]') : error.message;
  console.error(message);
  process.exitCode = 1;
});
