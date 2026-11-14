import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { createTranscriber, MAX_AUDIO_BYTES } from './transcribe.js';
import { registerAgentRoutes } from './routes.js';

const sample = { audio: Buffer.from('test recording'), mimeType: 'audio/webm;codecs=opus', language: 'zh-CN' };
test('transcription sends bounded audio multipart with latest model and language hint', async () => {
  const transcribe = createTranscriber({ apiKey: 'test-secret', fetchImpl: async (url, options) => {
    assert.equal(url, 'https://api.openai.com/v1/audio/transcriptions');
    assert.equal(options.headers.Authorization, 'Bearer test-secret');
    assert.equal(options.headers['Content-Type'], undefined); // fetch supplies multipart boundary.
    assert.equal(options.body.get('model'), 'gpt-transcribe');
    assert.equal(options.body.get('languages[]'), 'zh');
    assert.equal(options.body.get('language'), null);
    const audio = options.body.get('file');
    assert.equal(audio.name, 'speech.webm'); assert.equal(audio.type, 'audio/webm');
    assert.equal(await audio.text(), 'test recording');
    return { ok: true, json: async () => ({ text: ' 三明治。 ', languages: [{ code: 'zh' }] }) };
  } });
  assert.deepEqual(await transcribe(sample), { text: '三明治。' });
});
test('transcription rejects invalid input before provider calls and requires server key', async () => {
  let calls = 0;
  const transcribe = createTranscriber({ apiKey: '', fetchImpl: async () => { calls++; } });
  await assert.rejects(transcribe(sample), { status: 503 });
  await assert.rejects(transcribe({ ...sample, audio: Buffer.alloc(0) }), { status: 400 });
  await assert.rejects(transcribe({ ...sample, audio: Buffer.alloc(MAX_AUDIO_BYTES + 1) }), { status: 413 });
  await assert.rejects(transcribe({ ...sample, mimeType: 'text/plain' }), { status: 415 });
  await assert.rejects(transcribe({ ...sample, language: 'ja\r\nsecret' }), { status: 400 });
  assert.equal(calls, 0);
});
test('provider failures expose only safe status and never provider details', async () => {
  for (const [status, expected] of [[403, 502], [429, 429]]) {
    const transcribe = createTranscriber({ apiKey: 'secret', fetchImpl: async () => ({ ok: false, status, json: () => { throw Error('secret provider details'); } }) });
    await assert.rejects(transcribe(sample), (error) => error.status === expected && !error.message.includes('secret'));
  }
  for (const [name, expected] of [['TimeoutError', 504], ['TypeError', 502]]) {
    const transcribe = createTranscriber({ apiKey: 'secret', fetchImpl: async () => { throw Object.assign(Error('secret'), { name }); } });
    await assert.rejects(transcribe(sample), (error) => error.status === expected && !error.message.includes('secret'));
  }
  const malformed = createTranscriber({ apiKey: 'secret', fetchImpl: async () => ({ ok: true, json: async () => ({ text: 42 }) }) });
  await assert.rejects(malformed(sample), { status: 502 });
});
test('transcription honors explicit model override and accepts empty recognized speech', async () => {
  const transcribe = createTranscriber({ apiKey: 'secret', model: 'gpt-transcribe-custom', fetchImpl: async (_url, options) => {
    assert.equal(options.body.get('model'), 'gpt-transcribe-custom');
    assert.equal(options.body.get('languages[]'), null);
    assert.equal(options.body.get('file').name, 'speech.mp4');
    return { ok: true, json: async () => ({ text: '' }) };
  } });
  assert.deepEqual(await transcribe({ ...sample, mimeType: 'audio/mp4', language: undefined }), { text: '' });
});
test('raw audio HTTP route protects auth, enforces size and type, and passes language', async () => {
  let calls = 0;
  const app = express(); app.use(express.json({ limit: '64kb' }));
  registerAgentRoutes(app, { authorized: req => req.get('portal_token') === 'fixture', transcribe: async (input) => {
    calls++; assert.equal(input.audio.toString(), 'audio fixture');
    assert.equal(input.mimeType, 'audio/webm;codecs=opus'); assert.equal(input.language, 'ja-JP');
    return { text: 'お願いします。' };
  } });
  const server = app.listen(0, '127.0.0.1'); await new Promise(resolve => server.once('listening', resolve));
  const url = `http://127.0.0.1:${server.address().port}/api/agent/transcribe`;
  const headers = { 'content-type': 'audio/webm;codecs=opus', 'x-audio-language': 'ja-JP', portal_token: 'fixture' };
  try {
    assert.equal((await fetch(url, { method: 'POST', headers: { 'content-type': 'audio/webm' }, body: 'x' })).status, 401);
    assert.equal((await fetch(url, { method: 'POST', headers: { ...headers, 'content-type': 'text/plain' }, body: 'x' })).status, 415);
    const oversized = await fetch(url, { method: 'POST', headers, body: Buffer.alloc(MAX_AUDIO_BYTES + 1) });
    assert.equal(oversized.status, 413); assert.deepEqual(await oversized.json(), { error: 'Audio recording exceeds 10 MiB' });
    const compressed = await fetch(url, { method: 'POST', headers: { ...headers, 'content-encoding': 'gzip' }, body: 'invalid gzip' });
    assert.equal(compressed.status, 400);
    const valid = await fetch(url, { method: 'POST', headers, body: 'audio fixture' });
    assert.equal(valid.status, 200); assert.deepEqual(await valid.json(), { text: 'お願いします。' });
    assert.equal(calls, 1);
  } finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
});
