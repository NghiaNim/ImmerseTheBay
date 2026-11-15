// Real Cartesia managed voice path with synthesized microphone fixtures.
// Does not claim acoustic echo/noise validation or real Dynamic rendering.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs');
const assert = require('node:assert/strict');
const live = process.argv.includes('--live');
const base = process.env.PORTAL_URL || 'http://localhost:8792';
const artifacts = process.env.E2E_ARTIFACTS || '/tmp/il-voice-e2e';
(async () => {
  const browser = await chromium.launch({headless:true, executablePath:process.env.CHROME_PATH || undefined,args:['--autoplay-policy=no-user-gesture-required']});
  const page = await browser.newPage({viewport:{width:1440,height:900}});
  const events=[], turns=[], errors=[], responseReads=[];
  const started=Date.now();
  page.on('pageerror',e=>errors.push(e.message));
  page.on('websocket',ws=>ws.on('framereceived',({payload})=>{try {const e=JSON.parse(String(payload)); if(e.type!=='audio_output') events.push(e);}catch{}}));
  page.on('response',r=>{if(r.url().endsWith('/api/agent/turn')&&r.ok()) responseReads.push(r.json().then(data=>turns.push(data)));});
  await page.addInitScript(()=>{
    let ctx,dest;
    navigator.mediaDevices.getUserMedia=async()=>{
      ctx=new AudioContext({sampleRate:44100}); await ctx.resume();dest=ctx.createMediaStreamDestination();
      window.testMicStream=dest.stream; return dest.stream;
    };
    window.feedTestSpeech=async b64=>{
      const buffer=await ctx.decodeAudioData(Uint8Array.from(atob(b64),c=>c.charCodeAt(0)).buffer);
      const source=ctx.createBufferSource();source.buffer=buffer;source.connect(dest);source.start();
      await new Promise(resolve=>source.onended=resolve);
    };
  });
  const feed=async name=>page.evaluate(b64=>window.feedTestSpeech(b64),fs.readFileSync(`${artifacts}/${name}.wav`).toString('base64'));
  const speaking=()=>page.waitForFunction(()=>document.body.dataset.voiceState==='speaking',{}, {timeout:45000});
  const deadline=setTimeout(()=>void browser.close(),180000);
  try {
    await page.goto(`${base}/learn.html`);
    await page.waitForFunction(()=>!document.querySelector('#begin').disabled);
    await page.getByText('Guide language & session options',{exact:true}).click();
    await page.locator('#previewOnly').setChecked(!live);
    await page.locator('#begin').click();
    await page.waitForFunction(()=>!document.querySelector('#pinch').disabled || document.querySelector('#notice').classList.contains('error'),{}, {timeout:90000});
    assert.equal(await page.locator('#pinch').isDisabled(),false,await page.locator('#notice').innerText());
    await page.locator('#conversation').click();
    await speaking();
    await feed('sandwich');
    await page.waitForFunction(()=>document.querySelector('#routeStatus').textContent.includes('1/'),{}, {timeout:45000});
    await speaking();
    await feed('bottle');
    await page.waitForFunction(()=>/水|water|bottle/i.test(document.querySelector('#routeStatus').textContent+' '+document.querySelector('#decision').textContent),{}, {timeout:45000});
    await feed('return');
    await page.waitForFunction(()=>{try{return JSON.parse(document.querySelector('#decision').textContent).response.route.kind==='rejoin';}catch{return false;}},{},{timeout:45000});
    if (live) { await page.waitForTimeout(7000); await page.waitForFunction(() => { const v=document.querySelector('#video'); return v.videoWidth>0 && v.currentTime>0 && !v.paused; }); }
    await Promise.all(responseReads);
    const clears=events.filter(e=>e.type==='audio_output_clear');
    assert.ok(clears.length>=2,'Two actual provider interruption clears required');
    assert.ok(turns.some(t=>t.route.kind==='detour'));
    assert.equal(turns.at(-1).route.kind,'rejoin');
    assert.deepEqual(errors,[]);
    await page.screenshot({path:`${artifacts}/voice-rejoin.png`});
    await page.locator('#conversation').click();
    await page.waitForFunction(()=>document.querySelector('#conversationStatus').textContent==='Manual controls ready.');
    assert.equal(await page.locator('#pinch').isDisabled(),false);
    const report={realCartesia:true,microphone:'synthesized Japanese WAV via MediaStream',video:live?'real-dynamic':'practice',interruptions:clears.length,routes:turns.map(t=>t.route.kind),elapsedMs:Date.now()-started};
    fs.writeFileSync(`${artifacts}/${live?'live':'practice'}-voice-report.json`,JSON.stringify(report,null,2));
    console.log(JSON.stringify(report));
  } catch(error) {
    if(!page.isClosed()) console.error(await page.locator('#notice').innerText());
    throw error;
  } finally {
    if(!page.isClosed()) {
      if(await page.locator('#leave').isEnabled()) await page.evaluate(()=>document.querySelector('#leave').onclick());
      assert.ok(await page.evaluate(()=>!window.testMicStream||window.testMicStream.getTracks().every(t=>t.readyState==='ended')),'Microphone tracks must stop');
      fs.writeFileSync(`${artifacts}/${live?'live':'practice'}-voice-events.json`,JSON.stringify({events,turns,errors},null,2));
    }
    clearTimeout(deadline);await browser.close();
  }
})().catch(e=>{console.error(e.message);process.exitCode=1;});
