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
 assert.deepEqual(JSON.parse(request.body),{dynamic_variables:{...input,runId:'run-test',revision:'3',beat:'1',language:'ja'}});
});
test('reset during mint rejects stale session; provider errors are redacted',async t=>{
 let reads=0;
 const stale=await fixture(t,{snapshot:()=>++reads===1?state:null,fetchImpl:async()=>({ok:true,json:async()=>session})});
 assert.equal((await stale('token',input)).status,409);
 const bad=await fixture(t,{fetchImpl:async()=>{throw Error(env.CARTESIA_API_KEY);}});
 const r=await bad('token',input);assert.equal(r.status,502);assert.equal((await r.text()).includes(env.CARTESIA_API_KEY),false);
 const rate=await fixture(t,{fetchImpl:async()=>({ok:false,status:429})});assert.equal((await rate('token',input)).status,429);
});
