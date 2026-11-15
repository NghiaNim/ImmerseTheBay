// Real photo generation + real spoken-goal transcription. Rehearsal callbacks are simulated.
const {chromium}=require(process.env.PLAYWRIGHT_MODULE||'playwright');
const fs=require('node:fs');const assert=require('node:assert/strict');const path=require('node:path');
const base=process.env.PORTAL_URL||'http://localhost:8793',out='/tmp/il-modes-e2e';
(async()=>{
 const browser=await chromium.launch({headless:true,executablePath:process.env.CHROME_PATH||undefined,args:['--autoplay-policy=no-user-gesture-required']});
 const page=await browser.newPage({viewport:{width:1440,height:900}});page.setDefaultTimeout(45000);
 const reads=[],summaries=[],errors=[];let generated;
 page.on('pageerror',e=>errors.push(e.message));
 page.on('response',r=>{if(r.url().endsWith('/api/agent/summary')&&r.ok())reads.push(r.json().then(s=>summaries.push(s)));});
 await page.addInitScript(()=>{
  let context,destination;
  navigator.mediaDevices.getUserMedia=async()=>{context=new AudioContext({sampleRate:44100});await context.resume();destination=context.createMediaStreamDestination();window.goalStream=destination.stream;return destination.stream;};
  window.feedGoal=async b64=>{const buffer=await context.decodeAudioData(Uint8Array.from(atob(b64),c=>c.charCodeAt(0)).buffer);const source=context.createBufferSource();source.buffer=buffer;source.connect(destination);source.start();await new Promise(r=>source.onended=r);};
  window.SpeechRecognition=class{start(){this.onstart?.();setTimeout(()=>{this.onresult?.({resultIndex:0,results:[Object.assign([{transcript:window.testTranscript}],{isFinal:true})]});this.onend?.();},10);}stop(){this.onend?.();}abort(){this.onend?.();}};
 });
 const ready=()=>page.waitForFunction(()=>!document.querySelector('#pinch').disabled);
 const decision=()=>page.locator('#decision').textContent().then(JSON.parse).then(x=>x.response);
 const say=async text=>{await ready();const revision=(await decision()).operation.revision;await page.evaluate(t=>window.testTranscript=t,text);await page.locator('#speak').click();await page.waitForFunction(r=>{try{return JSON.parse(document.querySelector('#decision').textContent).response.operation.revision>r;}catch{return false;}},revision);};
 try{
  await page.goto(base+'/learn.html');await page.waitForFunction(()=>!document.querySelector('#begin').disabled);
  await page.getByText('Guide language & session options',{exact:true}).click();await page.locator('#speechEngine').selectOption('browser');
  await page.locator('#previewOnly').setChecked(true);await page.locator('#begin').click();await ready();
  await page.locator('#pinch').click();await ready();await page.locator('#endPractice').click();await page.locator('#practiceReview').waitFor({state:'visible'});await Promise.all(reads);
  assert.equal(summaries[0].scoredAttempts,0);assert.equal(summaries[0].averageTranscriptMatch,null);assert.equal(summaries[0].manualSkips,1);
  await page.locator('#newPractice').click();await page.locator('#modeReal').click();
  await page.locator('#realPhoto').setInputFiles(path.resolve(__dirname,'../../Assets/StreamingAssets/lessons/images/konbini-checkout-original.png'));
  await page.locator('#recordGoal').click();await page.waitForFunction(()=>!document.querySelector('#stopGoal').hidden&&!document.querySelector('#stopGoal').disabled);
  await page.evaluate(b64=>window.feedGoal(b64),fs.readFileSync(out+'/goal.wav').toString('base64'));await page.locator('#stopGoal').click();
  await page.waitForFunction(()=>document.querySelector('#practiceGoal').value.trim().length>0);
  assert.match(await page.locator('#practiceGoal').inputValue(),/水/);assert.ok(await page.evaluate(()=>window.goalStream.getTracks().every(t=>t.readyState==='ended')));
  const response=page.waitForResponse(r=>r.url().endsWith('/api/practice/generate'));await page.locator('#generatePractice').click();const r=await response;assert.equal(r.status(),200,await r.text());generated=(await r.json()).lesson;
  const count=generated.beats[0].lines.length;assert.ok(count>=2&&count<=4);assert.equal(typeof generated.visiblePartner,'boolean');
  await page.waitForFunction(()=>!document.querySelector('#begin').disabled);await page.screenshot({path:out+'/generated.png'});
  await page.locator('#begin').click();await ready();assert.equal(await page.locator('#romaji').isVisible(),true);
  await say('これは全く違う文です。');
  for(let i=0;i<count;i++){const d=await decision();await say(d.overlay.kana);}
  await page.locator('#practiceReview').waitFor({state:'visible'});await Promise.all(reads);
  const summary=summaries.at(-1);assert.equal(summary.completed,true);assert.equal(summary.scoredAttempts,count+1);assert.equal(summary.targetsCompleted,count);assert.equal(summary.firstAttempt.total,count);assert.equal(summary.firstAttempt.successes,count-1);assert.ok(summary.words.length>0);assert.deepEqual(errors,[]);
  assert.equal(await page.locator('#leave').isDisabled(),true);await page.screenshot({path:out+'/review.png'});
  fs.writeFileSync(out+'/report.json',JSON.stringify({generated,summaries,errors,goal:'synthetic Chinese mic through real server STT',rehearsal:'simulated recognition with exact generated kana',video:'practice'},null,2));
  console.log(JSON.stringify({homeSummary:true,realPhoto:true,realGoalTranscription:true,generatedLines:count,scoredAttempts:summary.scoredAttempts,firstAttempt:summary.firstAttempt,words:summary.words.length}));
 }catch(e){await page.screenshot({path:out+'/failure.png'});console.error(await page.locator('#notice').innerText());throw e;}
 finally{if(await page.locator('#leave').isEnabled())await page.evaluate(()=>document.querySelector('#leave').onclick());await browser.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
