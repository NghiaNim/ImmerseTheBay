import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createAgentService, validateRequest } from './service.js';
import { WIRE_RESPONSE_SCHEMA, RESPONSE_SCHEMA, validateSchema } from './schema.js';
const lesson=JSON.parse(await readFile(new URL('./fixtures/ja-konbini-001.json',import.meta.url)));
const key={sessionId:'preset-test',lessonId:lesson.id};
const req=(id,previous,changes={})=>({...key,beat:previous?.route.returnBeat||0,context:{},presetSceneSet:'konbini-original',imageRef:{id:'original-upload-123'},...changes,operation:{requestId:id,expectedRevision:previous?.operation.revision||0,...(previous?{runId:previous.operation.runId}:{})}});
const setup=options=>createAgentService({store:{load:async()=>lesson},llm:null,planner:null,log:async()=>{},promptIntervalMs:0,...options});
test('opted original scene switches deterministically, stays during steps, restores checkout and replays safely',async()=>{
 const service=setup();let r=await service.turn(req('open'));assert.equal(r.scene.id,'checkout');assert.ok(validateSchema(r,WIRE_RESPONSE_SCHEMA));assert.equal(validateSchema(r,RESPONSE_SCHEMA),false);
 const branch=req('branch',r,{intent:'I want a sandwich'});r=await service.turn(branch);assert.equal(r.scene.id,'sandwiches');assert.equal(r.scene.visiblePartner,false);assert.equal(r.scene.startImage,'/scene-presets/konbini-sandwiches.png');
 assert.deepEqual((await service.turn(branch)).scene,r.scene);assert.doesNotMatch(r.visko[0].data.prompt,/clerk|mouth|face|host|assistant/i);
 r=await service.turn(req('step',r,{context:{gesture:'pinch'}}));assert.equal(r.scene.id,'sandwiches');
 r=await service.turn(req('water',r,{intent:'I want a bottle of water'}));assert.equal(r.scene.id,'drinks');assert.equal(r.route.kind,'detour');
 const snapshot=service.snapshot(key);assert.equal(snapshot.lessonContext.visiblePartner,false);assert.match(snapshot.lessonContext.partnerLocation,/off-camera/);
 r=await service.turn(req('return',r,{action:'return_to_main'}));assert.equal(r.scene.id,'checkout');assert.equal(r.scene.visiblePartner,true);assert.equal(r.route.kind,'rejoin');
});
test('without opt in custom photos and other lessons cannot acquire scene metadata',async()=>{
 const service=setup();const request=req('custom',null,{presetSceneSet:undefined,imageRef:{id:'custom-photo'}});let r=await service.turn(request);assert.equal(r.scene,undefined);
 await assert.rejects(service.turn(req('late-opt-in',r)),{status:409});
 assert.throws(()=>validateRequest(req('wrong',null,{lessonId:'ja-home-001'})),{status:400});
 assert.throws(()=>validateRequest(req('wrongset',null,{presetSceneSet:'arbitrary'})),{status:400});
});
test('unknown intent preserves active scene and failed logging does not commit transition',async()=>{
 let fail=false;const service=setup({log:async()=>{if(fail)throw Error('disk');}});
 let r=await service.turn(req('open'));r=await service.turn(req('branch',r,{intent:'sandwich'}));
 r=await service.turn(req('unknown',r,{intent:'I want something unplanned'}));assert.equal(r.scene.id,'sandwiches');
 fail=true;await assert.rejects(service.turn(req('water',r,{intent:'water'})));assert.equal(service.snapshot(key).response.scene.id,'sandwiches');
 fail=false;r=await service.turn(req('water',r,{intent:'water'}));assert.equal(r.scene.id,'drinks');
});
test('finishing the drinks branch returns to checkout without changing main checkpoint',async()=>{
 const service=setup();let r=await service.turn(req('open'));
 r=await service.turn(req('tea',r,{intent:'tea'}));const checkpoint=r.route.returnBeat;
 r=await service.turn(req('drink-one',r,{transcript:r.overlay.jp}));assert.equal(r.scene.id,'drinks');
 r=await service.turn(req('drink-two',r,{transcript:r.overlay.jp}));assert.equal(r.scene.id,'checkout');assert.equal(r.route.kind,'rejoin');assert.equal(r.route.returnBeat,checkpoint);
});
test('classifier uses primary goal, ignores lexical false positives, preserves complex planning',async()=>{
 const {classifyPresetIntent,presetDrinkPlan}=await import('./scene-presets.js');
 assert.equal(classifyPresetIntent('water then return to the main lesson'),'drinks');
 assert.equal(classifyPresetIntent('I want water, then return to checkout'),'drinks');
 assert.equal(classifyPresetIntent('水曜日に買い物に行きたいです'),null);
 assert.equal(classifyPresetIntent('It is water under the bridge'),null);
 assert.equal(classifyPresetIntent('spill the tea'),null);
 assert.equal(classifyPresetIntent('お水を買いたいです'),'drinks');
 assert.equal(classifyPresetIntent('我想买水'),'drinks');
 assert.equal(classifyPresetIntent('Find the checkout counter'),'checkout');
 for(const intent of ['refund this bottle of water','What ingredients are in this tea?','exchange this bottle','このお茶の原材料を教えてください']) assert.equal(presetDrinkPlan(intent,0),null);
 assert.ok(presetDrinkPlan('I want a bottle of water',0));
});
test('explicit checkout branch selects counter image while preserving saved checkpoint',async()=>{
 const service=setup();let r=await service.turn(req('open'));
 r=await service.turn(req('water',r,{intent:'water'}));assert.equal(r.scene.id,'drinks');
 r=await service.turn(req('counter',r,{intent:'Find the checkout counter'}));assert.equal(r.scene.id,'checkout');assert.equal(r.route.kind,'detour');assert.equal(r.route.returnBeat,0);
});
