import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { createAgentService } from './service.js';
import { registerAgentRoutes } from './routes.js';
const line = jp => ({jp,kana:jp.replaceAll('水', 'みず').replaceAll('袋', 'ふくろ'),romaji:'practice',zh:'练习',en:'practice',cue:'The same host waits nearby.'});
const lesson={id:'ja-konbini-summary',title:'Convenience store',scenePrompt:'A clerk waits in a shop.',audioPrompt:'Quiet fridge hum.',beats:[{goal:'Buy a drink',lines:[line('水をください'),line('袋はいりません')],vocab:[{jp:'水',kana:'みず',en:'water'},{jp:'袋',kana:'ふくろ',en:'bag'},{jp:'入口',kana:'いりぐち',en:'entrance'}]}]};
const key={sessionId:'summary-session',lessonId:lesson.id};
const make=(id,previous,changes={})=>({...key,beat:previous?.route.returnBeat||0,context:{},...changes,operation:{requestId:id,expectedRevision:previous?.operation.revision||0,...(previous?{runId:previous.operation.runId}:{})}});
const setup=options=>createAgentService({store:{load:async()=>lesson},llm:null,planner:null,promptIntervalMs:0,log:async()=>{},...options});
test('summary tracks only encountered targets/vocabulary; no attempts means no score',async()=>{
 const service=setup();assert.equal(service.summary(key),null);await service.turn(make('open'));
 const s=service.summary(key);assert.equal(s.targetsEncountered,1);assert.equal(s.targetsAttempted,0);assert.equal(s.averageTranscriptMatch,null);assert.equal(s.firstAttempt.rate,null);assert.deepEqual(s.words.map(w=>w.jp),['水']);
 s.targets[0].jp='mutation';assert.equal(service.summary(key).targets[0].jp,'水をください');
});
test('scored attempts count first attempt and actual completions; replay counts once',async()=>{
 const service=setup();let r=await service.turn(make('open'));r=await service.turn(make('wrong',r,{transcript:'ちがいます'}));
 const req=make('correct',r,{transcript:'水をください'});r=await service.turn(req);await service.turn(req);
 let s=service.summary(key);assert.equal(s.scoredAttempts,2);assert.equal(s.targetsAttempted,1);assert.equal(s.targetsCompleted,1);assert.deepEqual(s.firstAttempt,{successes:0,total:1,rate:0});assert.deepEqual(s.words.map(w=>w.jp),['水','袋']);assert.equal(s.targetsEncountered,2);
 r=await service.turn(make('complete',r,{transcript:'袋はいりません'}));s=service.summary(key);assert.equal(r.advance,'scene_done');assert.equal(s.completed,true);assert.equal(s.targetsCompleted,2);assert.equal(s.scoredAttempts,3);assert.equal(s.firstAttempt.total,2);assert.equal(s.firstAttempt.successes,1);assert.equal(s.review.length,0);
});
test('intent, idle, hints and manual skips stay separate from scored practice',async()=>{
 const service=setup();let r=await service.turn(make('open'));r=await service.turn(make('idle',r,{action:'idle_hint'}));r=await service.turn(make('hint',r,{context:{gesture:'long_pinch'}}));r=await service.turn(make('branch',r,{intent:'I want a sandwich'}));r=await service.turn(make('skip',r,{transcript:r.overlay.jp,context:{gesture:'pinch'}}));r=await service.turn(make('return',r,{action:'return_to_main'}));
 const s=service.summary(key);assert.equal(s.scoredAttempts,0);assert.equal(s.hints,1);assert.equal(s.manualSkips,1);assert.equal(s.targetsCompleted,0);assert.ok(s.words.some(w=>w.jp==='サンドイッチ'));assert.equal(s.targetsEncountered,3);
});
test('failed log rolls back metrics; pending summary rejects; reset clears summary',async()=>{
 let fail=false,release;const service=setup({log:async()=>{if(fail)throw Error('disk');}});const r=await service.turn(make('open'));const before=service.summary(key);fail=true;
 await assert.rejects(service.turn(make('score',r,{transcript:r.overlay.jp})));assert.deepEqual(service.summary(key),before);fail=false;await service.turn(make('score',r,{transcript:r.overlay.jp}));assert.equal(service.summary(key).scoredAttempts,1);
 const waiting=setup({log:()=>new Promise(resolve=>{release=resolve;})});const pending=waiting.turn(make('pending'));await new Promise(resolve=>setImmediate(resolve));assert.throws(()=>waiting.summary(key),{status:409});release();await pending;assert.equal(waiting.summary(key).revision,1);waiting.reset(key);assert.equal(waiting.summary(key),null);
});
test('summary endpoint requires authorization and sends no-store',async t=>{
 const service=setup();await service.turn(make('open'));const app=express();app.use(express.json());registerAgentRoutes(app,{service,store:{},authorized:req=>req.get('portal_token')==='test'});
 const server=app.listen(0,'127.0.0.1');await new Promise(resolve=>server.once('listening',resolve));t.after(()=>new Promise(resolve=>server.close(resolve)));
 const request=token=>fetch(`http://127.0.0.1:${server.address().port}/api/agent/summary`,{method:'POST',headers:{'Content-Type':'application/json',portal_token:token},body:JSON.stringify(key)});
 assert.equal((await request('')).status,401);const r=await request('test');assert.equal(r.status,200);assert.equal(r.headers.get('cache-control'),'no-store');assert.equal((await r.json()).targetsEncountered,1);
});
test('rehearsing a high-match desire sentence scores rather than creating an inferred branch',async()=>{
 const target='サンドイッチが欲しいです';
 const custom=structuredClone(lesson);custom.beats[0].lines=[line(target)];
 const service=setup({store:{load:async()=>custom}});
 const r=await service.turn(make('desire-open'));
 const done=await service.turn(make('desire-score',r,{transcript:target}));
 assert.equal(done.advance,'scene_done');
 const s=service.summary(key);assert.equal(s.scoredAttempts,1);assert.equal(s.averageTranscriptMatch,100);assert.equal(s.firstAttempt.successes,1);assert.equal(s.targetsCompleted,1);
});

test('hinted and pointed transcripts are unscored and Wednesday does not imply water vocabulary',async()=>{
 const custom=structuredClone(lesson);
 custom.beats[0].lines=[{...line('水曜日に来ます'),kana:'すいようびにきます'}];
 const service=setup({store:{load:async()=>custom}});
 let r=await service.turn(make('weekday-open'));
 r=await service.turn(make('weekday-hint',r,{transcript:r.overlay.jp,context:{gesture:'long_pinch'}}));
 r=await service.turn(make('weekday-keyhint',r,{transcript:r.overlay.jp,context:{glassesKey:'hint'}}));
 r=await service.turn(make('weekday-point',r,{transcript:r.overlay.jp,context:{gesture:'point',pointedAt:'door'}}));
 const s=service.summary(key);
 assert.equal(s.scoredAttempts,0);assert.equal(s.averageTranscriptMatch,null);assert.equal(s.firstAttempt.total,0);assert.equal(s.hints,2);
 assert.equal(s.words.some(word=>word.en==='water'),false);
});

test('wire results identify the attempted sentence and keep branch and manual progress separate', async () => {
  const service = setup();
  let r = await service.turn(make('shared-open'));
  assert.equal(r.attempt, undefined);
  assert.deepEqual(r.progress, { mainTotal: 2, mainStep: 1, mainCompleted: 0 });
  r = await service.turn(make('shared-branch', r, { intent: 'I want a sandwich' }));
  assert.equal(r.attempt, undefined);
  assert.equal(r.route.kind, 'detour');
  assert.deepEqual(r.progress, { mainTotal: 2, mainStep: 1, mainCompleted: 0 });
  r = await service.turn(make('shared-return', r, { action: 'return_to_main' }));
  r = await service.turn(make('shared-wrong', r, { transcript: 'こんにちは' }));
  assert.equal(r.attempt.outcome, 'retry');
  assert.equal(r.attempt.targetId, 'main:0');
  r = await service.turn(make('shared-hint', r, { context: { gesture: 'long_pinch' } }));
  assert.equal(r.attempt, undefined);
  const request = make('shared-pass', r, { transcript: '水をください' });
  r = await service.turn(request);
  assert.equal(r.attempt.outcome, 'assisted');
  assert.equal(r.attempt.score, 100);
  assert.equal(r.attempt.jp, '水をください');
  assert.equal(r.attempt.nextTargetId, 'main:1');
  assert.equal(r.overlay.jp, '袋はいりません');
  assert.deepEqual(r.progress, { mainTotal: 2, mainStep: 2, mainCompleted: 1 });
  const retry = await service.turn(request);
  assert.deepEqual(retry.attempt, r.attempt);
  assert.deepEqual(retry.progress, r.progress);
  r = await service.turn(make('shared-skip', r, { context: { glassesKey: 'next' } }));
  assert.equal(r.attempt.outcome, 'skip');
  assert.equal(r.attempt.score, null);
  assert.equal(r.attempt.text, '');
  assert.equal(r.attempt.nextTargetId, null);
  assert.deepEqual(r.progress, { mainTotal: 2, mainStep: 2, mainCompleted: 1 });
  assert.equal(service.summary(key).scoredAttempts, 2);
});

test('explicit rehearsal scores every answer, including off-target shopping requests and the final line', async () => {
 const refinements = [];
 const service = setup({llm: async ({ fallback }) => { refinements.push(true); return fallback; }});
 const practice = {practiceAttempt: true};
 const unrelatedRequest = 'I want a sandwich';
 await assert.rejects(service.turn(make('early', null, {...practice, transcript: unrelatedRequest})), {status:409});
 let r = await service.turn(make('open'));
 await assert.rejects(service.turn(make('mixed', r, {...practice, transcript: unrelatedRequest, intent: unrelatedRequest})), {status:400});

 const wrong = make('wrong', r, {...practice, transcript: unrelatedRequest});
 r = await service.turn(wrong);
 assert.equal(r.route.kind, 'main');
 assert.equal(r.attempt.outcome, 'retry');
 assert.equal(r.attempt.text, unrelatedRequest);
 assert.deepEqual(r.visko, []);
 assert.deepEqual((await service.turn(wrong)).attempt, r.attempt);

 r = await service.turn(make('branch', r, {intent: unrelatedRequest}));
 assert.equal(r.route.kind, 'detour');
 r = await service.turn(make('branch-rehearsal', r, {...practice, transcript: r.overlay.jp}));
 assert.equal(r.attempt.outcome, 'pass');
 r = await service.turn(make('return', r, {action:'return_to_main'}));
 r = await service.turn(make('correct', r, {...practice, transcript: r.overlay.jp}));
 assert.equal(r.attempt.outcome, 'pass');
 r = await service.turn(make('finish', r, {...practice, transcript: r.overlay.jp}));
 assert.equal(r.advance, 'scene_done');
 assert.equal(r.attempt.outcome, 'pass');
 assert.equal(r.attempt.nextTargetId, null);
 assert.equal(service.summary(key).scoredAttempts, 4);
 assert.equal(refinements.length, 1);
});
