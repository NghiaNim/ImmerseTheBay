import test from 'node:test';
import assert from 'node:assert/strict';
import { createLessonSpeech } from './lesson-speech.js';

const tick = () => new Promise((resolve) => setTimeout(resolve, 0));
function setup(t, { fetchImpl, playError } = {}) {
  const calls = [], audios = [], states = [], revoked = [];
  const savedAudio = globalThis.Audio;
  globalThis.Audio = class {
    constructor(url) { this.url = url; audios.push(this); }
    play() { return playError ? Promise.reject(playError) : Promise.resolve(); }
    pause() { this.paused = true; }
    removeAttribute() {}
    load() {}
  };
  t.after(() => { if (savedAudio === undefined) delete globalThis.Audio; else globalThis.Audio = savedAudio; });
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    calls.push({ url, init });
    return fetchImpl ? fetchImpl(url, init) : { ok: true, blob: async () => new Blob(['audio'], { type: 'audio/wav' }) };
  });
  t.mock.method(URL, 'createObjectURL', () => `blob:fixture-${audios.length}`);
  t.mock.method(URL, 'revokeObjectURL', (url) => revoked.push(url));
  const video = { volume: 0.8 }, microphone = { active: false };
  const player = createLessonSpeech({ video, headers: () => ({ portal_token: 'fixture-token' }), isMicrophoneActive: () => microphone.active, onState: (state) => states.push(state) });
  t.after(() => player.cancel());
  return { player, video, microphone, calls, audios, states, revoked };
}

test('microphone excludes playback; the authenticated endpoint distinguishes learner examples and clerk dialogue', async (t) => {
  const { player, microphone, calls, audios } = setup(t);
  microphone.active = true;
  assert.equal(await player.play({ text: 'Blocked during microphone' }), false);
  assert.equal(calls.length, 0);
  microphone.active = false;
  const playback = player.play({ text: '新しい返事。', language: 'ja', role: 'clerk' });
  await tick();
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, '/api/agent/speak');
  assert.equal(calls[0].init.headers.portal_token, 'fixture-token');
  assert.deepEqual(JSON.parse(calls[0].init.body), { text: '新しい返事。', language: 'ja' });
  audios[0].onended(); assert.equal(await playback, true);
  const example = player.play({ text: 'はい。' });
  await tick();
  assert.deepEqual(JSON.parse(calls[1].init.body), { text: 'はい。', language: 'ja', role: 'tutor' });
  audios[1].onended(); assert.equal(await example, true);
});

test('canceled in-flight audio response never plays, including a fetch that ignores abort', async (t) => {
  let resolveFetch;
  const { player, audios, video, calls } = setup(t, { fetchImpl: () => new Promise((resolve) => { resolveFetch = resolve; }) });
  const playback = player.play({ text: 'Old reply', role: 'clerk' });
  player.cancel();
  assert.equal(calls[0].init.signal.aborted, true);
  resolveFetch({ ok: true, blob: async () => new Blob(['audio'], { type: 'audio/wav' }) });
  assert.equal(await playback, false);
  assert.equal(audios.length, 0);
  assert.equal(video.volume, 0.8);
});

test('playback completion and cancellation restore exact scene volume and release blob URLs', async (t) => {
  const { player, audios, video, revoked } = setup(t);
  const first = player.play({ text: 'First' }); await tick();
  assert.equal(video.volume, 0.8 * 0.15);
  audios[0].onended(); assert.equal(await first, true);
  assert.equal(video.volume, 0.8); assert.equal(revoked.length, 1);
  video.volume = 0.35;
  const second = player.play({ text: 'Second' }); await tick();
  assert.equal(video.volume, 0.35 * 0.15);
  player.cancel(); assert.equal(await second, false);
  assert.equal(video.volume, 0.35); assert.equal(revoked.length, 2);
});

test('autoplay rejection restores volume and exposes replayable error state', async (t) => {
  const { player, video, states, revoked } = setup(t, { playError: new DOMException('User gesture required', 'NotAllowedError') });
  assert.equal(await player.play({ text: 'Clerk reply', role: 'clerk' }), false);
  assert.equal(video.volume, 0.8); assert.equal(revoked.length, 1);
  assert.equal(states.at(-1).state, 'error');
  assert.match(states.at(-1).message, /replay/);
});
