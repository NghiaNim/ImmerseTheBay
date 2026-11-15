import test from 'node:test';
import assert from 'node:assert/strict';
import { createCartesiaSpeech, MAX_SPEECH_BYTES } from './cartesia.js';
const wav = Buffer.from('RIFF\x08\x00\x00\x00WAVE');
const options = { apiKey: 'secret-fixture', voiceId: 'voice-fixture' };
const sample = { text: '  いらっしゃいませ。  ' };
const audioResponse = (bytes = wav) => new Response(bytes, { headers: { 'content-type': 'audio/wav' } });
test('Cartesia uses server key, pinned API version, voice ID string and bounded WAV request', async () => {
  let calls = 0;
  const speak = createCartesiaSpeech({ ...options, fetchImpl: async (url, init) => {
    calls++;
    assert.equal(url, 'https://api.cartesia.ai/tts/bytes');
    assert.equal(init.headers.Authorization, 'Bearer secret-fixture');
    assert.equal(init.headers['Cartesia-Version'], '2026-08-14');
    assert.deepEqual(JSON.parse(init.body), { model_id: 'sonic-latest', transcript: 'いらっしゃいませ。', voice: 'voice-fixture', output_format: { container: 'wav', encoding: 'pcm_s16le', sample_rate: 44100 }, language: 'ja' });
    return audioResponse();
  } });
  assert.deepEqual(await speak(sample), wav); assert.equal(calls, 1);
});
test('invalid input, absent configuration and already canceled requests never reach provider', async () => {
  let calls = 0;
  const fetchImpl = async () => { calls++; return audioResponse(); };
  for (const body of [{}, { text: '' }, { text: ' '.repeat(5) }, { text: 'x'.repeat(1001) }, { ...sample, language: 'fr' }, { ...sample, signal: {} }]) {
    await assert.rejects(createCartesiaSpeech({ ...options, fetchImpl })(body), { status: 400 });
  }
  for (const config of [{ apiKey: '', voiceId: 'voice' }, { apiKey: 'key', voiceId: '' }]) await assert.rejects(createCartesiaSpeech({ ...config, fetchImpl })(sample), { status: 503 });
  await assert.rejects(createCartesiaSpeech({ ...options, fetchImpl })({ ...sample, signal: AbortSignal.abort() }), { status: 499 });
  assert.equal(calls, 0);
});
test('provider failures are redacted and configurable model, version and language are passed', async () => {
  for (const [status, expected] of [[401, 502], [403, 502], [429, 429], [503, 503], [500, 502]]) {
    const speak = createCartesiaSpeech({ ...options, fetchImpl: async () => new Response('secret-fixture and provider internals', { status }) });
    await assert.rejects(speak(sample), error => error.status === expected && !/secret|internals/.test(error.message));
  }
  const speak = createCartesiaSpeech({ ...options, model: 'explicit-model', apiVersion: 'explicit-version', fetchImpl: async (_url, init) => {
    assert.equal(init.headers['Cartesia-Version'], 'explicit-version');
    assert.equal(JSON.parse(init.body).model_id, 'explicit-model'); assert.equal(JSON.parse(init.body).language, 'en');
    return audioResponse();
  } });
  assert.deepEqual(await speak({ text: 'Hello.', language: 'en' }), wav);
  await assert.rejects(createCartesiaSpeech({ ...options, fetchImpl: async () => { throw Error('secret-fixture'); } })(sample), error => error.status === 502 && !error.message.includes('secret'));
});
test('response cap applies to declared and streamed bytes; malformed non-audio is rejected', async () => {
  let canceled = false;
  const stream = () => new ReadableStream({ start(controller) { controller.enqueue(Buffer.alloc(MAX_SPEECH_BYTES)); controller.enqueue(Buffer.from('x')); }, cancel() { canceled = true; } });
  await assert.rejects(createCartesiaSpeech({ ...options, fetchImpl: async () => new Response(stream()) })(sample), /exceeds size limit/);
  assert.equal(canceled, true);
  canceled = false;
  await assert.rejects(createCartesiaSpeech({ ...options, fetchImpl: async () => new Response(stream(), { headers: { 'content-length': String(MAX_SPEECH_BYTES + 1) } }) })(sample), /exceeds size limit/);
  assert.equal(canceled, true);
  for (const bytes of [Buffer.alloc(0), Buffer.from('{"provider":"secret-fixture"}')]) await assert.rejects(createCartesiaSpeech({ ...options, fetchImpl: async () => audioResponse(bytes) })(sample), { status: 502 });
});
test('two-slot admission rejects excess work and cancellation releases a slot', async () => {
  let calls = 0;
  const speak = createCartesiaSpeech({ ...options, fetchImpl: async () => {
    calls++;
    if (calls > 2) return audioResponse();
    return new Promise(() => {});
  } });
  const one = new AbortController(); const two = new AbortController();
  const pendingOne = speak({ ...sample, signal: one.signal });
  const pendingTwo = speak({ ...sample, signal: two.signal });
  await assert.rejects(speak(sample), { status: 429 });
  const canceledOne = assert.rejects(pendingOne, { status: 499 }); one.abort(); await canceledOne;
  assert.deepEqual(await speak(sample), wav);
  const canceledTwo = assert.rejects(pendingTwo, { status: 499 }); two.abort(); await canceledTwo;
  assert.equal(calls, 3);
});
test('deadline and caller cancellation abort a stalled body without buffering forever', async () => {
  let canceled = 0; let upstreamSignal;
  const fetchImpl = async (_url, init) => {
    upstreamSignal = init.signal;
    return new Response(new ReadableStream({ start(controller) { controller.enqueue(wav); }, cancel() { canceled++; } }));
  };
  const speak = createCartesiaSpeech({ ...options, fetchImpl, timeoutMs: 10 });
  await assert.rejects(speak(sample), { status: 504 });
  assert.equal(upstreamSignal.aborted, true); assert.equal(canceled, 1);
  const controller = new AbortController();
  const pending = createCartesiaSpeech({ ...options, fetchImpl })({ ...sample, signal: controller.signal });
  await new Promise(resolve => setTimeout(resolve, 0));
  const rejected = assert.rejects(pending, { status: 499 }); controller.abort(); await rejected;
  assert.equal(upstreamSignal.aborted, true); assert.equal(canceled, 2);
});

test('finalizes streaming WAV sizes after metadata chunks for file decoders', async () => {
  const streamed = Buffer.concat([
    Buffer.from('52494646ffffffff57415645', 'hex'),
    Buffer.from('4c49535403000000494e4600', 'hex'),
    Buffer.from('64617461ffffffff01000200', 'hex'),
  ]);
  const speak = createCartesiaSpeech({ ...options, fetchImpl: async () => audioResponse(streamed) });
  const complete = await speak(sample);
  assert.equal(complete.readUInt32LE(4), complete.length - 8);
  assert.equal(complete.readUInt32LE(28), 4);
  assert.deepEqual(complete.subarray(32), streamed.subarray(32));
});
