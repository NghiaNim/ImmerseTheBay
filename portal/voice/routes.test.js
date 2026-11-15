import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { registerVoiceRoutes, voiceConfig } from './routes.js';
const env = { CARTESIA_API_KEY: 'private-test-key', CARTESIA_AGENT_ID: 'agent_test' };
const state = { runId: 'run-test', revision: 3, beat: 1, response: { advance: 'stay' } };
async function fixture(t, options = {}) {
 const app=express(); app.use(express.json());
 registerVoiceRoutes(app,{env,authorized:req=>req.get('portal_token')==='test-auth',snapshot:()=>state,...options});
 const server=app.listen(0,'127.0.0.1');await new Promise(r=>server.once('listening',r));
 t.after(()=>new Promise(r=>server.close(r)));
 return (path,body,auth=true)=>fetch(`http://127.0.0.1:${server.address().port}/api/voice/${path}`,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json',...(auth?{portal_token:'test-auth'}:{})},...(body===undefined?{}:{body:JSON.stringify(body)})});
}
const input={sessionId:'session',lessonId:'konbini'};
const session={url:'wss://api-us.cartesia.ai/v1/agents/websocket/agent_test',token:'single-use',call_id:'call-test'};
test('config authenticates and excludes API key',async t=>{
 assert.equal(voiceConfig({}).configured,false);
 const call=await fixture(t); assert.equal((await call('config',undefined,false)).status,401);
 const r=await call('config');assert.equal(r.headers.get('cache-control'),'no-store');
 assert.deepEqual(await r.json(),{configured:true,provider:'cartesia',agentId:'agent_test'});
});
test('token requires auth, configuration, active lesson and strict input',async t=>{
 const call=await fixture(t,{fetchImpl:()=>{throw Error('must not fetch');}});
 assert.equal((await call('token',input,false)).status,401);
 assert.equal((await call('token',{...input,url:'https://attacker'})).status,400);
 const absent=await fixture(t,{env:{}});assert.equal((await absent('token',input)).status,503);
 const inactive=await fixture(t,{snapshot:()=>null});assert.equal((await inactive('token',input)).status,409);
});
test('trusted lesson binding is sent only from server snapshot',async t=>{
 let request;
 const call=await fixture(t,{fetchImpl:async(url,options)=>{request={url,...options};return{ok:true,json:async()=>session};}});
 const r=await call('token',input);assert.equal(r.status,200);
 assert.deepEqual(await r.json(),{url:session.url,token:session.token,callId:session.call_id,agentId:'agent_test',version:'2026-08-14',runId:'run-test',revision:3});
 assert.equal(request.headers['X-API-Key'],env.CARTESIA_API_KEY);
 assert.deepEqual(JSON.parse(request.body),{dynamic_variables:{...input,runId:'run-test',revision:'3',beat:'1',language:'ja',practiceContext:JSON.stringify({scene:'',goal:'',learnerLine:''}),opening:'いらっしゃいませ。何をお探しですか？'}});
});
test('reset during mint rejects stale session; provider errors are redacted',async t=>{
 let reads=0;
 const stale=await fixture(t,{snapshot:()=>++reads===1?state:null,fetchImpl:async()=>({ok:true,json:async()=>session})});
 assert.equal((await stale('token',input)).status,409);
 const bad=await fixture(t,{fetchImpl:async()=>{throw Error(env.CARTESIA_API_KEY);}});
 const r=await bad('token',input);assert.equal(r.status,502);assert.equal((await r.text()).includes(env.CARTESIA_API_KEY),false);
 const rate=await fixture(t,{fetchImpl:async()=>({ok:false,status:429})});assert.equal((await rate('token',input)).status,429);
});
test('off-camera preset scene is trusted voice context',async t=>{
 let context;
 const call=await fixture(t,{snapshot:()=>({...state,lessonContext:{scene:'Drinks shelf',goal:'Buy water',visiblePartner:false,partnerLocation:'off-camera; voice only'}}),fetchImpl:async(_url,options)=>{
  context=JSON.parse(JSON.parse(options.body).dynamic_variables.practiceContext);return{ok:true,json:async()=>session};
 }});
 assert.equal((await call('token',input)).status,200);
 assert.equal(context.visiblePartner,false);assert.equal(context.partnerLocation,'off-camera; voice only');assert.equal(context.scene,'Drinks shelf');
});
test('managed voice uses the pinned snapshot style and rejects client routing fields', async t => {
 const agents = [];
 const pairedEnv = { CARTESIA_API_KEY: env.CARTESIA_API_KEY,
  CARTESIA_AGENT_ID_FEMALE: 'agent_female', CARTESIA_AGENT_ID_MALE: 'agent_male' };
 let partnerVoice = 'female';
 const call = await fixture(t, {
  env: pairedEnv,
  snapshot: () => ({ ...state, lessonContext: { partnerVoice } }),
  fetchImpl: async url => {
   const agentId = url.split('/').at(-2);
   agents.push(agentId);
   return { ok: true, json: async () => ({ ...session, url: session.url.replace('agent_test', agentId) }) };
  },
 });
 assert.equal((await call('config')).status, 200);
 for (const style of ['female', 'male', 'male']) {
  partnerVoice = style;
  const response = await call('token', input);
  assert.equal(response.status, 200);
  assert.equal((await response.json()).agentId, `agent_${style}`);
 }
 assert.equal((await call('token', { ...input, partnerVoice: 'female' })).status, 400);
 assert.equal((await call('token', { ...input, agentId: 'agent_female' })).status, 400);
 assert.deepEqual(agents, ['agent_female', 'agent_male', 'agent_male']);
});
test('managed voice never silently substitutes a different agent for a pinned style', async t => {
 const call = await fixture(t, {
  snapshot: () => ({ ...state, lessonContext: { partnerVoice: 'male' } }),
  fetchImpl: () => { assert.fail('a different agent must not be called'); },
 });
 assert.equal((await call('token', input)).status, 503);
});
test('a new pinned style invalidates a managed token minted against the old style', async t => {
 let partnerVoice = 'female';
 const call = await fixture(t, {
  env: { ...env, CARTESIA_AGENT_ID_FEMALE: 'agent_female', CARTESIA_AGENT_ID_MALE: 'agent_male' },
  snapshot: () => ({ ...state, lessonContext: { partnerVoice } }),
  fetchImpl: async () => {
   partnerVoice = 'male';
   return { ok: true, json: async () => session };
  },
 });
 assert.equal((await call('token', input)).status, 409);
});
