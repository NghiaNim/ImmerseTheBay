// Regression only: delay one authored preset GET; no model, STT or video request.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const path = require('node:path');
(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH || undefined });
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.setDefaultTimeout(15000);
  const errors = [], providerRequests = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (/\/api\/(practice\/generate|voice\/token|agent\/(turn|transcribe|speak))/.test(request.url())) providerRequests.push(request.url()); });
  let release;
  try {
    await page.goto((process.env.PORTAL_URL || 'http://localhost:8793') + '/learn.html');
    await page.waitForFunction(() => !document.querySelector('#begin').disabled);
    const initial = await page.locator('#lesson').inputValue();
    const next = await page.locator('#lesson option').evaluateAll((options, selected) => options.find(option => option.value !== selected).value, initial);
    let captured, delivered;
    const requestCaptured = new Promise(resolve => { captured = resolve; });
    const responseDelivered = new Promise(resolve => { delivered = resolve; });
    const gate = new Promise(resolve => { release = resolve; });
    await page.route(`**/api/agent/lessons/${next}`, async route => {
      const response = await route.fetch(); captured(); await gate;
      await route.fulfill({ response }); delivered();
    });
    await page.locator('#lesson').selectOption(next);
    await requestCaptured;
    assert.equal(await page.locator('#begin').isDisabled(), true);
    await page.locator('#leave').click();
    await page.locator('#modeReal').click();
    await page.locator('#realPhoto').setInputFiles(path.resolve(__dirname, '../../Assets/StreamingAssets/lessons/images/konbini-checkout-original.png'));
    await page.locator('#practiceGoal').fill('Ask for directions in this scene.');
    const before = await page.evaluate(() => ({ photo: document.querySelector('#photoPreview').src, label: document.querySelector('#lessonLabel').textContent, sentence: document.querySelector('#jp').textContent, romaji: document.querySelector('#romaji').textContent }));
    release(); await responseDelivered;
    // Let fetch continuation and the following render complete without a wall-clock sleep.
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const after = await page.evaluate(() => ({ photo: document.querySelector('#photoPreview').src, label: document.querySelector('#lessonLabel').textContent, sentence: document.querySelector('#jp').textContent, romaji: document.querySelector('#romaji').textContent }));
    assert.deepEqual(after, before);
    assert.equal(after.label, 'REAL-WORLD JAPANESE');
    assert.equal(await page.locator('#modeReal').getAttribute('aria-pressed'), 'true');
    assert.equal(await page.locator('#begin').isDisabled(), true);
    assert.equal(await page.locator('#generatePractice').isEnabled(), true);
    assert.deepEqual(errors, []); assert.deepEqual(providerRequests, []);
    console.log(JSON.stringify({ stalePresetIgnored: true, realPhotoPreserved: true, realLabelsPreserved: true, beginDisabledUntilGeneration: true, providerRequests: 0 }));
  } finally { release?.(); await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
