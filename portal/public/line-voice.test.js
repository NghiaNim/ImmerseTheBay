import test from 'node:test';
import assert from 'node:assert/strict';
import { createLineVoice, encodePcm, decodePcm, assertVoiceRevision } from './line-voice.js';
function fixture(onTool = async () => ({ accepted: true }), getUserMedia) {
  let stopped = 0, closed = 0; const sources = [], states = [];
  class Socket { static instance; constructor() { Socket.instance = this; this.readyState = 1; this.bufferedAmount = 0; this.sent = []; } send(data) { this.sent.push(JSON.parse(data)); } close() { closed++; } emit(data) { this.onmessage?.({ data: JSON.stringify(data) }); } }
  const node = () => ({ connect() {}, disconnect() {} });
  class Context { constructor() { this.sampleRate = 44100; this.currentTime = 1; } async resume() {} async close() {} createMediaStreamSource() { return node(); } createScriptProcessor() { return node(); } createGain() { return { ...node(), gain: {} }; } createBuffer(ch, length, rate) { return { duration: length / rate, copyToChannel() {} }; } createBufferSource() { const source = { ...node(), start() {}, stop() { source.stopped = true; } }; sources.push(source); return source; } }
  const voice = createLineVoice({ WebSocketImpl: Socket, AudioContextImpl: Context, getSession: async () => ({ url: 'wss://example.test/voice', token: 'opaque', version: '2026-08-14' }), getUserMedia: getUserMedia || (async () => ({ getTracks: () => [{ stop() { stopped++; } }] })), onState: s => states.push(s.state), onTool });
  return { voice, sources, states, socket: () => Socket.instance, counts: () => ({ stopped, closed }) };
}
const flush = () => new Promise(resolve => setImmediate(resolve));
test('PCM16 mono preserves signed samples and clips peaks', () => {
  const decoded = decodePcm(encodePcm(new Float32Array([-2, -0.5, 0, 0.5, 2])));
  assert.deepEqual([...decoded].map(n => Math.round(n * 100)), [-100, -50, 0, 50, 100]);
  assert.throws(() => decodePcm(btoa('x')));
});
test('barge-in stops current and buffered audio; disconnect releases mic/socket', async () => {
  const f = fixture(); await f.voice.connect(); const socket = f.socket(); socket.onopen(); socket.emit({ type: 'session_ready' });
  assert.equal(socket.sent[0].type, 'session_create');
  socket.emit({ type: 'audio_output', audio: encodePcm(new Float32Array(441)) }); socket.emit({ type: 'audio_output', audio: encodePcm(new Float32Array(441)) });
  assert.equal(f.voice.state, 'speaking'); socket.emit({ type: 'audio_output_clear' });
  assert.ok(f.sources.every(source => source.stopped)); assert.equal(f.voice.state, 'listening');
  f.voice.disconnect(); assert.deepEqual(f.counts(), { stopped: 1, closed: 1 }); assert.equal(f.voice.state, 'off');
});
test('tool calls are serialized and duplicate IDs never execute twice', async () => {
  const calls = []; let release; const gate = new Promise(resolve => { release = resolve; });
  const f = fixture(async call => { calls.push(call.tool_call_id); if (call.tool_call_id === 'a') await gate; return { accepted: true }; });
  await f.voice.connect(); const socket = f.socket();
  for (const id of ['a', 'a', 'b']) socket.emit({ type: 'client_tool_call', tool_call_id: id, expects_response: true });
  await flush(); assert.deepEqual(calls, ['a']); release(); await flush(); assert.deepEqual(calls, ['a', 'b']); assert.equal(socket.sent.length, 2); f.voice.disconnect();
});
test('late permission result after disconnect stops stream without connecting', async () => {
  let resolve, stopped = 0; const f = fixture(undefined, () => new Promise(r => { resolve = r; }));
  const connect = f.voice.connect(); await flush(); f.voice.disconnect(); resolve({ getTracks: () => [{ stop() { stopped++; } }] }); await connect;
  assert.equal(stopped, 1); assert.equal(f.voice.state, 'off');
});
test('disconnect drops queued tools and stale results without undoing an issued tool', async () => {
  let release; const calls = []; const f = fixture(async call => { calls.push(call.tool_call_id); await new Promise(r => { release = r; }); return {}; });
  await f.voice.connect(); const socket = f.socket(); for (const id of ['a', 'b']) socket.emit({ type: 'client_tool_call', tool_call_id: id, expects_response: true });
  await flush(); f.voice.disconnect(); release(); await flush(); assert.deepEqual(calls, ['a']); assert.equal(socket.sent.length, 0);
});

test('barge-in drops unissued stale tools but preserves already issued decision and new tools', async () => {
  let release; const calls = []; const f = fixture(async call => { calls.push(call.tool_call_id); if (call.tool_call_id === 'a') await new Promise(r => { release = r; }); return { accepted: true }; });
  await f.voice.connect(); const socket = f.socket();
  for (const id of ['a', 'stale']) socket.emit({ type: 'client_tool_call', tool_call_id: id, expects_response: true });
  await flush(); socket.emit({ type: 'audio_output_clear' }); socket.emit({ type: 'client_tool_call', tool_call_id: 'new', expects_response: true });
  release(); await flush(); assert.deepEqual(calls, ['a', 'new']); assert.equal(socket.sent.find(event => event.tool_call_id === 'stale').is_error, true); f.voice.disconnect();
});

test('voice token and disconnect snapshot require exact local run and revision', () => {
  const local = { runId: 'run-a', revision: 3 };
  assert.doesNotThrow(() => assertVoiceRevision({ ...local }, local));
  for (const remote of [null, { runId: 'run-b', revision: 3 }, { runId: 'run-a', revision: 4 }, { runId: 'run-a', revision: 2 }]) assert.throws(() => assertVoiceRevision(remote, local), /Leave and restart/);
  assert.throws(() => assertVoiceRevision(local, null));
});
