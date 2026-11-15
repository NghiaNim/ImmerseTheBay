import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, symlink, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createAgentService, validateRequest } from './service.js';
import { createLessonStore, validateLesson } from './lessons.js';
import { createClaudeClient, DEFAULT_MODEL } from './llm.js';
import { normalizeKana, scoreTranscript, safePrompt, hasBannedTerm } from './guardrails.js';
import { composeVisualPrompt } from './continuity.js';
import { validateSchema } from './schema.js';
const line = (jp, kana, cue) => ({ jp, kana, cue, romaji: 'romaji', zh: '中文', en: 'English' });
const lesson = { id: 'ja-test', title: 'Original home', level: 'N1', startImage: 'lessons/images/home.jpg', scenePrompt: 'A host in a blue shirt sits beside a low table in a tatami room. Static eye-level camera.', audioPrompt: 'Rain taps the roof.', beats: [
  { goal: 'Ask to sit', lines: [line('座ってもいいですか。', 'すわってもいいですか。', 'The host points toward a cushion.'), line('ありがとうございます。', 'ありがとうございます。', 'The host places a cup on the table.')], vocab: [{ jp: '机', kana: 'つくえ', en: 'table', objectHint: 'table' }] },
  { goal: 'Leave', lines: [line('失礼します。', 'しつれいします。', 'The host slides the door open.')], vocab: [{ jp: '戸', kana: 'と', en: 'door', objectHint: 'door' }] },
] };
const request = (overrides = {}) => ({ sessionId: 'test-session', lessonId: 'ja-test', beat: 0, context: {}, ...overrides });
const setup = (options = {}) => { const logs = []; return { logs, service: createAgentService({ store: { load: async () => structuredClone(lesson) }, llm: null, planner: null, promptIntervalMs: 0, log: async (e) => logs.push(e), ...options }) }; };
test('first turn is native before start, no key required, canonical overlay', async () => {
  const { service, logs } = setup(); const r = await service.turn(request());
  assert.deepEqual(r.visko.map((c) => c.name), ['set_prompt', 'set_audio_prompt', 'set_resolution', 'start']);
  assert.deepEqual(r.visko.at(-2).data, { resolution: 'native' });
  assert.equal(r.overlay.jp, lesson.beats[0].lines[0].jp); assert.equal(r.advance, 'stay'); assert.ok(validateSchema(r)); assert.equal(logs[0].source, 'scripted');
});
test('speech advances flattened lines, then completes with null overlay', async () => {
  const { service } = setup(); await service.turn(request());
  let r = await service.turn(request({ transcript: 'スワッテモイイデスカ' }));
  assert.equal(r.feedback.score, 100); assert.equal(r.advance, 'next_line'); assert.equal(r.overlay.jp, lesson.beats[0].lines[1].jp);
  r = await service.turn(request({ beat: 1, transcript: 'ありがとうございます' })); assert.equal(r.overlay.jp, lesson.beats[1].lines[0].jp);
  r = await service.turn(request({ beat: 2, transcript: '失礼します' })); assert.equal(r.advance, 'scene_done'); assert.equal(r.overlay, null); assert.equal(r.visko[0].name, 'pause');
});
test('wrong speech stays; hint and point do not accidentally advance', async () => {
  const { service } = setup(); await service.turn(request());
  const wrong = await service.turn(request({ transcript: '別の言葉' })); assert.equal(wrong.advance, 'stay'); assert.ok(wrong.overlay.hintLevel > 0);
  const hinted = await service.turn(request({ transcript: 'すわってもいいですか', context: { gesture: 'long_pinch' } })); assert.equal(hinted.advance, 'stay');
  const pointed = await service.turn(request({ context: { gesture: 'point', pointedAt: 'table' } })); assert.match(pointed.feedback.notes.join(' '), /つくえ/); assert.match(pointed.visko[0].data.prompt, /table/);
  const pinch = await service.turn(request({ context: { gesture: 'pinch' } })); assert.equal(pinch.advance, 'next_line');
});
test('room, desk, gaze and RMS assistive signals affect response', async () => {
  const { service } = setup(); await service.turn(request());
  const r = await service.turn(request({ context: { roomLabels: ['table'], planes: { horizontalAtDeskHeight: true }, gaze: 'room', rms: 0.001 } }));
  assert.match(r.visko[0].data.prompt, /table stays in view/); assert.ok(r.overlay.hintLevel > 0); assert.match(r.feedback.notes.join(' '), /desk-height/); assert.match(r.feedback.notes.join(' '), /Speak when ready/);
});
test('LLM gets caption/context but cannot replace score, authored overlay or progression', async () => {
  let seen; const { service } = setup({ llm: async (input) => { seen = input; const r = structuredClone(input.fallback); r.visko[0].data.prompt = 'The host pours tea.'; r.overlay.jp = 'wrong'; r.feedback = { score: 1000, notes: ['Try a softer ending.'] }; r.advance = 'scene_done'; return r; } });
  const r = await service.turn(request({ imageRef: { id: 'opaque-ref', caption: 'Low table beside a window.' }, context: { roomLabels: ['table'] } }));
  assert.equal(seen.photoCaption, 'Low table beside a window.'); assert.equal(r.visko[0].data.prompt, composeVisualPrompt(lesson.scenePrompt)); assert.equal(r.advance, 'stay'); assert.equal(r.overlay.jp, lesson.beats[0].lines[0].jp); assert.equal(r.feedback.score, 0);
});
test('LLM errors and malformed strict JSON fall back deterministically', async () => {
  for (const llm of [async () => { throw new Error('provider secret'); }, async () => ({ visko: [] }), async ({ fallback }) => ({ ...fallback, unknown: 'field' })]) {
    const { service, logs } = setup({ llm }); const r = await service.turn(request()); assert.equal(r.visko[0].data.prompt, composeVisualPrompt(lesson.scenePrompt)); assert.equal(logs[0].source, 'scripted'); assert.doesNotMatch(JSON.stringify(logs), /provider secret/);
  }
});
test('banned terms and long prompts repaired, including Unicode obfuscation', async () => {
  assert.ok(hasBannedTerm('Ｄｏｒａｅｍｏｎ')); assert.ok(hasBannedTerm('Dora\u200bemon')); assert.equal(safePrompt('Doraemon appears.', 'safe'), 'safe');
  assert.ok(safePrompt('One. Two. Three. Four.', 'safe').split('.').length <= 4); assert.ok(safePrompt('word '.repeat(400), 'safe').length <= 600);
  const { service } = setup({ llm: async ({ fallback }) => { fallback.visko[0].data.prompt = 'Doraemon enters.'; return fallback; } });
  assert.equal((await service.turn(request())).visko[0].data.prompt, composeVisualPrompt(lesson.scenePrompt));
});
test('session serialization rate limits prompts even across lesson IDs', async () => {
  let clock = 1000; const waits = [];
  const { service } = setup({ promptIntervalMs: 2000, now: () => clock, sleep: async (ms) => { waits.push(ms); clock += ms; } });
  const results = await Promise.all([service.turn(request()), service.turn(request({ lessonId: 'ja-other' })), service.turn(request({ context: { gesture: 'long_pinch' } }))]);
  assert.deepEqual(waits, [2000, 2000]); assert.equal(results.length, 3);
});
test('failed logging does not mark scene started or poison session queue', async () => {
  let fail = true; const { service } = setup({ log: async () => { if (fail) { fail = false; throw new Error('disk unavailable'); } } });
  await assert.rejects(service.turn(request())); const r = await service.turn(request()); assert.ok(r.visko.some((v) => v.name === 'start'));
});
test('input bounds reject traversal, excessive scoring work and malformed context', async () => {
  for (const changes of [{ lessonId: '../private' }, { beat: -1 }, { transcript: 'x'.repeat(1001) }, { context: { rms: 2 } }, { context: { planes: {} } }, { context: { gesture: 'bad' } }, { context: { roomLabels: [42] } }, { imageRef: { id: 'x', url: 'https://bad' } }]) assert.throws(() => validateRequest(request(changes)), { status: 400 });
  const { service } = setup(); await assert.rejects(service.turn(request({ beat: 3 })), { status: 400 });
});
test('kana scoring folds katakana and punctuation; does not guess kanji readings', () => {
  assert.equal(normalizeKana('スワッテ、モ！'), 'すわっても'); assert.equal(scoreTranscript('座ってもいいですか', lesson.beats[0].lines[0]), 100); assert.equal(scoreTranscript('', lesson.beats[0].lines[0]), 0);
});
test('lesson store excludes private and symlink files, serves only flat image formats', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'il-lessons-'));
  try {
    await mkdir(path.join(root, 'private')); await mkdir(path.join(root, 'images'));
    await writeFile(path.join(root, 'ja-test.json'), JSON.stringify(lesson)); await writeFile(path.join(root, 'private', 'secret.json'), '{}'); await writeFile(path.join(root, 'images', 'home.jpg'), 'test');
    const store = createLessonStore(root); assert.equal((await store.list()).length, 1); assert.equal((await store.load('ja-test')).id, 'ja-test'); assert.match(await store.image('home.jpg'), /home.jpg$/);
    await assert.rejects(store.load('../private/secret'), { status: 400 }); await assert.rejects(store.image('SOURCES.md'), { status: 404 });
    await symlink(path.join(root, 'private', 'secret.json'), path.join(root, 'hidden.json')); await assert.rejects(store.load('hidden'), { status: 404 });
    assert.throws(() => validateLesson({ ...lesson, scenePrompt: 'Doraemon appears.' }), { status: 422 });
  } finally { await rm(root, { recursive: true, force: true }); }
});
test('Claude adapter uses official endpoint, auth, schema and text blocks only', async () => {
  let captured;
  const llm = createClaudeClient({ apiKey: 'fake-test-key', fetchImpl: async (url, init) => { captured = { url, init, body: JSON.parse(init.body) }; return { ok: true, json: async () => ({ content: [{ type: 'thinking', thinking: 'ignored' }, { type: 'text', text: '{"ok":true}' }] }) }; } });
  assert.deepEqual(await llm({ lesson, current: {}, transcript: '', context: {}, photoCaption: 'caption', fallback: {}, attempts: 1 }), { ok: true });
  assert.equal(captured.url, 'https://api.anthropic.com/v1/messages'); assert.equal(captured.body.model, DEFAULT_MODEL); assert.equal(captured.body.output_config.format.type, 'json_schema'); assert.equal(captured.init.headers['x-api-key'], 'fake-test-key');
});
test('malformed lesson lines/vocab return 422 rather than crashing', () => {
  for (const bad of [null, { ...lesson, beats: [null] }, { ...lesson, beats: [{ ...lesson.beats[0], lines: [null] }] }, { ...lesson, beats: [{ ...lesson.beats[0], vocab: [null] }] }]) assert.throws(() => validateLesson(bad), { status: 422 });
});
test('reset initializes a joined scene again without resetting prompt rate limit', async () => {
  let clock = 0; let waited = 0; const { service } = setup({ now: () => clock, promptIntervalMs: 2000, sleep: async (ms) => { waited += ms; clock += ms; } });
  await service.turn(request()); assert.deepEqual(service.reset({ sessionId: 'test-session', lessonId: 'ja-test' }), { ok: true });
  const result = await service.turn(request()); assert.ok(result.visko.some((v) => v.name === 'start')); assert.equal(waited, 2000);
});
test('invalid lessons do not consume session capacity permanently', async () => {
  const { service } = setup({ maxSessions: 1, store: { load: async (id) => { if (id === 'missing') { const e = new Error('not found'); e.status = 404; throw e; } return lesson; } } });
  await assert.rejects(service.turn(request({ sessionId: 'bad', lessonId: 'missing' })), { status: 404 });
  assert.equal((await service.turn(request())).advance, 'stay');
});
test('real home lesson romaji does not trigger character-name substrings', async () => {
  const { readFile } = await import('node:fs/promises');
  // Original authored content copied from the integrated lesson, not rewritten to avoid detection.
  const home = JSON.parse(await readFile(new URL('./fixtures/ja-home-001.json', import.meta.url), 'utf8'));
  assert.equal(validateLesson(home).id, 'ja-home-001');
  assert.equal(hasBannedTerm('Tenjou made naosu to naru to, sasuga ni watashitachi dake de wa te ni oemasen.'), false);
  assert.equal(hasBannedTerm('tonaruto'), false);
  for (const name of ['Naruto enters.', 'Ｄｏｒａｅｍｏｎ', 'Dora\u200bemon', 'D.o.r.a.e.m.o.n', 'Mickey Mouse', 'ドラえもん']) assert.equal(hasBannedTerm(name), true, name);
});
test('gesture navigation skips model latency while opening and speech still request feedback', async () => {
  let calls = 0; const { service } = setup({ llm: async ({ fallback }) => { calls++; return fallback; } });
  await service.turn(request()); assert.equal(calls, 1);
  const next = await service.turn(request({ context: { gesture: 'pinch' } })); assert.equal(next.advance, 'next_line'); assert.equal(calls, 1);
  await service.turn(request({ beat: 1, transcript: lesson.beats[0].lines[1].kana })); assert.equal(calls, 2);
});
