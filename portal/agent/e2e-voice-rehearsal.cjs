// Real Cartesia STT/managed tool/TTS with synthetic Japanese mic; no Visko session.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('node:fs');
const assert = require('node:assert/strict');
(async()=>{
 const base=process.env.PORTAL_URL||'http://localhost:8793';
 const artifacts=process.env.E2E_ARTIFACTS||'/tmp/il-voice-rehearsal';fs.mkdirSync(artifacts,{recursive:true});
 const browser=await chromium.launch({headless:true,executablePath:process.env.CHROME_PATH,args:['--autoplay-policy=no-user-gesture-required']});
 const page=await browser.newPage({viewport:{width:1440,height:900}});
 const events=[],turns=[],errors=[];let binding,report;const started=Date.now();
 page.on('pageerror',error=>errors.push(error.message));
 page.on('websocket',ws=>ws.on('framereceived',({payload})=>{try{const event=JSON.parse(String(payload));if(event.type!=='audio_output')events.push(event);}catch{}}));
 page.on('request',request=>{if(request.url().endsWith('/api/agent/turn')){const body=request.postDataJSON();binding={sessionId:body.sessionId,lessonId:body.lessonId};}});
 page.on('response',async response=>{if(response.url().endsWith('/api/agent/turn')&&response.ok())turns.push(await response.json());});
 await page.addInitScript(()=>{
  let ctx,dest;
  navigator.mediaDevices.getUserMedia=async()=>{ctx=new AudioContext({sampleRate:44100});await ctx.resume();dest=ctx.createMediaStreamDestination();window.testMicStream=dest.stream;return dest.stream;};
  window.feedTestSpeech=async b64=>{const buffer=await ctx.decodeAudioData(Uint8Array.from(atob(b64),c=>c.charCodeAt(0)).buffer);const source=ctx.createBufferSource();source.buffer=buffer;source.connect(dest);source.start();await new Promise(resolve=>source.onended=resolve);};
 });
 const deadline=setTimeout(()=>void browser.close(),105000);
 try{
  await page.goto(`${base}/learn.html`);await page.waitForFunction(()=>!document.querySelector('#begin').disabled);
  await page.locator('#previewOnly').setChecked(true);await page.locator('#begin').click();
  await page.waitForFunction(()=>!document.querySelector('#pinch').disabled,{}, {timeout:30000});
  const target=await page.locator('#jp').innerText();
  const audio=await page.request.post(`${base}/api/agent/speak`,{data:{text:target,language:'ja'},timeout:30000});assert.equal(audio.status(),200);
  const wav=await audio.body();fs.writeFileSync(`${artifacts}/target.wav`,wav);
  await page.locator('#conversation').click();
  await page.waitForFunction(()=>document.body.dataset.voiceState==='listening'||document.body.dataset.voiceState==='speaking',{}, {timeout:20000});
  const greetingDeadline=Date.now()+20000;while(!events.some(event=>event.type==='turn_ended'&&event.role==='assistant')&&Date.now()<greetingDeadline)await page.waitForTimeout(200);
  await page.evaluate(b64=>window.feedTestSpeech(b64),wav.toString('base64'));
  const toolDeadline=Date.now()+30000;while(turns.length<2&&Date.now()<toolDeadline)await page.waitForTimeout(200);
  const summaryResponse=await page.request.post(`${base}/api/agent/summary`,{data:binding});assert.equal(summaryResponse.status(),200);
  const summary=await summaryResponse.json();
  const calls=events.filter(event=>event.type==='client_tool_call');
  report={realCartesia:true,video:'practice-only',microphone:'synthesized current Japanese target via MediaStream',target,tools:calls.map(call=>({name:call.tool_name,parameters:call.parameters})),transcripts:events.filter(event=>event.type==='turn_ended'&&event.role==='user').map(event=>event.text),routes:turns.map(turn=>turn.route.kind),summary,errors,elapsedMs:Date.now()-started};
  fs.writeFileSync(`${artifacts}/report.json`,JSON.stringify(report,null,2));
  assert.ok(calls.some(call=>call.tool_name==='rehearse_line'),'Expected actual rehearse_line client tool');
  assert.ok(!calls.some(call=>call.tool_name==='branch_scene'),'Exact target must not branch');
  assert.equal(summary.scoredAttempts,1);assert.ok(summary.averageTranscriptMatch>=75);assert.equal(summary.targetsCompleted,1);assert.equal(summary.firstAttempt.successes,1);assert.deepEqual(errors,[]);
  console.log(JSON.stringify(report));
 }finally{
  if(!page.isClosed()){
   await page.evaluate(()=>document.querySelector('#leave')?.onclick());
   assert.ok(await page.evaluate(()=>!window.testMicStream||window.testMicStream.getTracks().every(track=>track.readyState==='ended')),'Microphone must release');
   fs.writeFileSync(`${artifacts}/events.json`,JSON.stringify({events,turns,errors},null,2));
  }
  clearTimeout(deadline);await browser.close();
 }
})().catch(error=>{console.error(error.message);process.exitCode=1;});
