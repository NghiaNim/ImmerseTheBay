import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { registerAgentRoutes } from './routes.js';
import { createCartesiaSpeech, MAX_SPEECH_BYTES, stereoSpeechWav } from './cartesia.js';
import { AgentError } from './lessons.js';
const wav = Buffer.from('RIFF\x08\x00\x00\x00WAVE');
async function withServer(synthesize, run, service) {
  const app = express(); app.use(express.json());
  registerAgentRoutes(app, { authorized: req => req.get('portal_token') === 'fixture', synthesize, service });
  const server = app.listen(0, '127.0.0.1');
  await new Promise(resolve => server.once('listening', resolve));
  const url = `http://127.0.0.1:${server.address().port}/api/agent/speak`;
  const headers = { 'content-type': 'application/json', portal_token: 'fixture' };
  try { await run({ url, headers, post: body => fetch(url, { method: 'POST', headers, body: JSON.stringify(body) }) }); }
  finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
}
function speechWav(channels, samples, { streaming = false, paddedMetadata = false } = {}) {
  const header = Buffer.from('524946460000000057415645666d7420100000000100010044ac00008858010002001000', 'hex');
  header.writeUInt16LE(channels, 22);
  header.writeUInt32LE(44100 * channels * 2, 28);
  header.writeUInt16LE(channels * 2, 32);
  const metadata = paddedMetadata ? Buffer.from('4a554e4b0300000061626300', 'hex') : Buffer.alloc(0);
  const body = Buffer.alloc(8 + samples.length * 2);
  body.write('data', 0);
  body.writeUInt32LE(streaming ? 0xffffffff : body.length - 8, 4);
  samples.forEach((sample, index) => body.writeInt16LE(sample, 8 + index * 2));
  const result = Buffer.concat([header, metadata, body]);
  result.writeUInt32LE(streaming ? 0xffffffff : result.length - 8, 4);
  return result;
}

test('both XREAL routes duplicate every mono sample into explicit left and right channels', async () => {
  const samples = [-32768, 0, 4, 32767];
  const source = speechWav(1, samples, { streaming: true, paddedMetadata: true });
  const service = { snapshot: () => ({
    runId: 'current-run', revision: 2, lessonContext: { partnerVoice: 'female' },
    response: { speech: { role: 'clerk', text: 'はい。', language: 'ja' } },
  }) };
  const voices = [];
  const speak = createCartesiaSpeech({
    apiKey: 'fixture-key', voiceId: 'default-fixture', femaleVoiceId: 'female-fixture',
    fetchImpl: async (_url, init) => {
      voices.push(JSON.parse(init.body).voice);
      return new Response(source);
    },
  });
  await withServer(speak, async ({ url, headers, post }) => {
    const body = { text: 'はい。', language: 'ja' };
    const web = Buffer.from(await (await post(body)).arrayBuffer());
    assert.equal(web.readUInt16LE(22), 1);
    assert.equal(web.readUInt32LE(4), source.length - 8);
    assert.deepEqual(web.subarray(-samples.length * 2), source.subarray(-samples.length * 2));
    const xr = { ...headers, 'x-tenin-audio-layout': 'stereo' };
    for (const [endpoint, payload] of [
      [url, body],
      [url.replace(/speak$/, 'clerk-speech'), { sessionId: 'fixture', lessonId: 'test', runId: 'current-run', revision: 2 }],
    ]) {
      const response = await fetch(endpoint, { method: 'POST', headers: xr, body: JSON.stringify(payload) });
      assert.equal(response.status, 200);
      const stereo = Buffer.from(await response.arrayBuffer());
      assert.deepEqual(stereo, speechWav(2, samples.flatMap(sample => [sample, sample])));
      assert.equal(response.headers.get('cache-control'), 'no-store');
    }
  }, service);
  assert.deepEqual(voices, ['default-fixture', 'default-fixture', 'female-fixture']);
});

test('existing stereo passes through and invalid or oversized opted-in audio is rejected', async () => {
  const stereo = speechWav(2, [100, -100, 200, -200], { paddedMetadata: true });
  assert.equal(stereoSpeechWav(stereo), stereo);
  const invalid = [
    wav,
    stereo.subarray(0, stereo.length - 1),
    speechWav(2, [42]),
    speechWav(1, [42]),
    speechWav(1, [42]),
  ];
  invalid[3].writeUInt16LE(3, 20); // IEEE float must not be treated as PCM16.
  invalid[4].writeUInt32LE(0, 24); // Sample rate.
  for (const malformed of invalid) assert.throws(() => stereoSpeechWav(malformed), { status: 502, message: 'Invalid speech audio' });
  const oversized = Buffer.alloc(MAX_SPEECH_BYTES / 2 + 44);
  speechWav(1, [0]).copy(oversized, 0, 0, 44);
  oversized.writeUInt32LE(oversized.length - 8, 4);
  oversized.writeUInt32LE(oversized.length - 44, 40);
  assert.throws(() => stereoSpeechWav(oversized), { status: 502, message: 'Speech response exceeds size limit' });

  let calls = 0;
  await withServer(async () => { calls++; return wav; }, async ({ url, headers }) => {
    const request = layout => fetch(url, { method: 'POST', headers: { ...headers, 'x-tenin-audio-layout': layout }, body: '{"text":"hello"}' });
    assert.equal((await request('other')).status, 400);
    assert.equal(calls, 0);
    const response = await request('stereo');
    assert.equal(response.status, 502);
    assert.deepEqual(await response.json(), { error: 'Invalid speech audio' });
  });
});
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
