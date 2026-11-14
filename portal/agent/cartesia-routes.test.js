import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { registerAgentRoutes } from './routes.js';
import { createCartesiaSpeech } from './cartesia.js';
import { AgentError } from './lessons.js';
const wav = Buffer.from('RIFF\x08\x00\x00\x00WAVE');
async function withServer(synthesize, run) {
  const app = express(); app.use(express.json());
  registerAgentRoutes(app, { authorized: req => req.get('portal_token') === 'fixture', synthesize });
  const server = app.listen(0, '127.0.0.1');
  await new Promise(resolve => server.once('listening', resolve));
  const url = `http://127.0.0.1:${server.address().port}/api/agent/speak`;
  const headers = { 'content-type': 'application/json', portal_token: 'fixture' };
  try { await run({ url, headers, post: body => fetch(url, { method: 'POST', headers, body: JSON.stringify(body) }) }); }
  finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
}
test('speech HTTP authorization and closed body shape protect synthesis; successful WAV is not cached', async () => {
  const calls = [];
  await withServer(async value => { calls.push(value); return wav; }, async ({ url, headers, post }) => {
    const unauthorized = await fetch(url, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ text: 'hello' }) });
    assert.equal(unauthorized.status, 401);
    for (const body of [[], { text: 'hello', voice: 'client-override' }, { text: 'hello', apiKey: 'client-secret' }, { text: 'hello', signal: {} }]) {
      const response = await post(body); assert.equal(response.status, 400);
    }
    assert.equal(calls.length, 0);
    const result = await post({ text: 'いらっしゃいませ。', language: 'ja' });
    assert.equal(result.status, 200); assert.match(result.headers.get('content-type'), /^audio\/wav/);
    assert.equal(result.headers.get('cache-control'), 'no-store'); assert.deepEqual(Buffer.from(await result.arrayBuffer()), wav);
    assert.equal(calls.length, 1); assert.equal(calls[0].text, 'いらっしゃいませ。'); assert.equal(calls[0].language, 'ja');
    assert.ok(calls[0].signal instanceof AbortSignal); assert.equal(calls[0].signal.aborted, false);
  });
});
test('missing server configuration and unexpected synthesis errors return redacted HTTP errors', async () => {
  const synthesize = createCartesiaSpeech({ apiKey: '', voiceId: '', fetchImpl: async () => { throw Error('must not call provider'); } });
  await withServer(synthesize, async ({ post }) => {
    const response = await post({ text: 'hello' }); assert.equal(response.status, 503);
    assert.deepEqual(await response.json(), { error: 'Server speech requires CARTESIA_API_KEY and CARTESIA_VOICE_ID' });
    assert.equal((await post({ text: 42 })).status, 400);
    assert.equal((await post({ text: 'hello', language: 'fr' })).status, 400);
  });
  await withServer(async () => { throw Error('private provider key: secret-fixture'); }, async ({ post }) => {
    const response = await post({ text: 'hello' }); assert.equal(response.status, 500);
    assert.deepEqual(await response.json(), { error: 'Learning service unavailable' });
  });
});
test('disconnecting the HTTP caller propagates cancellation to speech synthesis', async () => {
  let started; let canceled;
  const didStart = new Promise(resolve => { started = resolve; });
  const didCancel = new Promise(resolve => { canceled = resolve; });
  await withServer(({ signal }) => new Promise((resolve, reject) => {
    signal.addEventListener('abort', () => { canceled(); reject(new AgentError(499, 'Speech request canceled')); }, { once: true });
    started();
  }), async ({ url, headers }) => {
    const controller = new AbortController();
    const pending = fetch(url, { method: 'POST', headers, body: JSON.stringify({ text: 'hello' }), signal: controller.signal });
    await didStart;
    const rejected = assert.rejects(pending, { name: 'AbortError' }); controller.abort(); await rejected;
    let timer;
    try { await Promise.race([didCancel, new Promise((_, reject) => { timer = setTimeout(() => reject(Error('Speech cancellation did not propagate')), 1000); })]); }
    finally { clearTimeout(timer); }
  });
});
