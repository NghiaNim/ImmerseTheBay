// Real Cartesia managed voice path with synthesized microphone fixtures.
// --live verifies real Dynamic playback separately from visual prompt fidelity.
// Synthesized microphone fixtures do not validate human microphone acoustics.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs');
const assert = require('node:assert/strict');
const live = process.argv.includes('--live');
const base = process.env.PORTAL_URL || 'http://localhost:8793';
const artifacts = process.env.E2E_ARTIFACTS || '/tmp/il-voice-e2e';
(async () => {
  const browser = await chromium.launch({headless:true, executablePath:process.env.CHROME_PATH || undefined,args:['--autoplay-policy=no-user-gesture-required']});
  const page = await browser.newPage({viewport:{width:1440,height:900}});
  const events=[], turns=[], errors=[], responseReads=[];
  const started=Date.now(), frames=[];
  fs.mkdirSync(artifacts,{recursive:true});
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
  const capture=async name=>{
    if(!live)return;
    await page.waitForFunction(()=>{const v=document.querySelector('#video');return v.videoWidth>0&&!v.paused&&v.currentTime>0;},{},{timeout:20000});
    const frame=await page.locator('#video').evaluate(v=>{const c=document.createElement('canvas');c.width=v.videoWidth;c.height=v.videoHeight;c.getContext('2d').drawImage(v,0,0);return {width:v.videoWidth,height:v.videoHeight,time:v.currentTime,png:c.toDataURL('image/png').split(',')[1]};});
    fs.writeFileSync(`${artifacts}/joint-${name}.png`,Buffer.from(frame.png,'base64'));
    frames.push({name,width:frame.width,height:frame.height,time:frame.time});
  };
  const deadline=setTimeout(()=>void browser.close(),180000);
  try {
    await page.goto(`${base}/learn.html`);
    await page.waitForFunction(()=>!document.querySelector('#begin').disabled);
    await page.getByText('Guide language & session options',{exact:true}).click();
    await page.locator('#previewOnly').setChecked(!live);
    await page.locator('#begin').click();
    await page.waitForFunction(()=>!document.querySelector('#pinch').disabled || document.querySelector('#notice').classList.contains('error'),{}, {timeout:90000});
    assert.equal(await page.locator('#pinch').isDisabled(),false,await page.locator('#notice').innerText());
    await capture('opening');
    await page.locator('#conversation').click();
    await speaking();
    await feed('sandwich');
    await page.waitForFunction(()=>document.querySelector('#routeStatus').textContent.includes('1/'),{}, {timeout:45000});
    await capture('sandwich');
    await speaking();
    await feed('bottle');
    await page.waitForFunction(()=>/水|water|bottle/i.test(document.querySelector('#routeStatus').textContent+' '+document.querySelector('#decision').textContent),{}, {timeout:45000});
    await capture('water');
    await feed('return');
    await page.waitForFunction(()=>{try{return JSON.parse(document.querySelector('#decision').textContent).response.route.kind==='rejoin';}catch{return false;}},{},{timeout:45000});
    if (live) { await page.waitForTimeout(7000); await page.waitForFunction(() => { const v=document.querySelector('#video'); return v.videoWidth>0 && v.currentTime>0 && !v.paused; }); }
    await capture('rejoin');
    if(live) assert.ok(frames.at(-1).time>frames[0].time+10,'Video must advance through the conversation');
    await Promise.all(responseReads);
    const clears=events.filter(e=>e.type==='audio_output_clear');
    assert.ok(clears.length>=2,'Two actual provider interruption clears required');
    const interrupted=events.filter(e=>e.type==='turn_ended'&&e.role==='assistant'&&e.interrupted);
    assert.ok(interrupted.length>=2,'Provider must confirm two interrupted assistant turns');
    assert.deepEqual(turns.map(t=>t.operation.revision),[1,2,3,4]);
    assert.equal(turns[1].route.returnBeat,turns[3].route.returnBeat);
    assert.notEqual(turns[1].route.nodeId,turns[2].route.nodeId);
    assert.ok(turns.some(t=>t.route.kind==='detour'));
    assert.equal(turns.at(-1).route.kind,'rejoin');
    assert.deepEqual(errors,[]);
    await page.screenshot({path:`${artifacts}/voice-rejoin.png`});
    await page.locator('#conversation').click();
    await page.waitForFunction(()=>document.querySelector('#conversationStatus').textContent==='Manual controls ready.');
    assert.equal(await page.locator('#pinch').isDisabled(),false);
    const report={realCartesia:true,microphone:'synthesized Japanese WAV via MediaStream',video:live?'real-dynamic':'practice',interruptions:clears.length,interruptedAssistantTurns:interrupted.length,frames,routes:turns.map(t=>t.route.kind),elapsedMs:Date.now()-started};
    fs.writeFileSync(`${artifacts}/${live?'live':'practice'}-voice-report.json`,JSON.stringify(report,null,2));
    console.log(JSON.stringify(report));
  } catch(error) {
    if(!page.isClosed()) console.error(await page.locator('#notice').innerText());
    throw error;
  } finally {
    if(!page.isClosed()) {
      if(await page.locator('#leave').isEnabled()) await page.evaluate(()=>document.querySelector('#leave').onclick());
      assert.doesNotMatch(await page.locator('#notice').innerText(),/Disconnect did not confirm/);
      assert.equal(await page.locator('#leave').isDisabled(),true);
      assert.ok(await page.evaluate(()=>!window.testMicStream||window.testMicStream.getTracks().every(t=>t.readyState==='ended')),'Microphone tracks must stop');
      fs.writeFileSync(`${artifacts}/${live?'live':'practice'}-voice-events.json`,JSON.stringify({events,turns,errors},null,2));
    }
    clearTimeout(deadline);await browser.close();
  }
})().catch(e=>{console.error(e.message);process.exitCode=1;});
