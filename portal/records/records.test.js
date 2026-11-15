import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { mkdtemp, rm, readFile, readdir } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { sanitizeJpeg } from './jpeg.js';
import { createRecordStore, withReplays } from './store.js';
import { createSessionRecorder } from './recorder.js';
import { registerRecordRoutes } from './routes.js';
import { summarizeEvents, compileReplay } from './compile.js';
import { createAgentService } from '../agent/service.js';

const segment = (marker, payload) => { const b = Buffer.alloc(4 + payload.length); b[0] = 0xff; b[1] = marker; b.writeUInt16BE(payload.length + 2, 2); payload.copy(b, 4); return b; };
const sof = (w, h) => { const p = Buffer.alloc(15); p[0] = 8; p.writeUInt16BE(h, 1); p.writeUInt16BE(w, 3); p[5] = 3; return p; };
function fixtureJpeg({ width = 1280, height = 720, exif = true } = {}) {
  return Buffer.concat([
    Buffer.from([0xff, 0xd8]),
    segment(0xe0, Buffer.from('JFIF\0\x01\x01\0\0\x01\0\x01\0\0', 'binary')),
    ...(exif ? [segment(0xe1, Buffer.from('Exif\0\0GPS 37.77N 122.41W'))] : []),
    segment(0xfe, Buffer.from('comment')),
    segment(0xc0, sof(width, height)),
    segment(0xda, Buffer.from([1, 1, 0, 0, 0x3f, 0])), Buffer.from([0x12, 0x34, 0xff, 0x00, 0x56]),
    Buffer.from([0xff, 0xd9]),
  ]);
}
const dataUrl = (buf) => `data:image/jpeg;base64,${buf.toString('base64')}`;

const line = (jp, kana, cue) => ({ jp, kana, cue, romaji: 'romaji', zh: '中文', en: 'English' });
const lesson = { id: 'ja-konbini-test', title: 'Convenience store', level: 'N1', startImage: 'lessons/images/shop.png', scenePrompt: 'An adult clerk stands behind a convenience-store counter. Static camera.', audioPrompt: 'Quiet store ambience.', beats: [
  { goal: 'Greet', lines: [line('こんにちは。', 'こんにちは。', 'The clerk nods.'), line('お願いします。', 'おねがいします。', 'The clerk smiles.')], vocab: [] },
  { goal: 'Pay', lines: [line('カードで払います。', 'かーどではらいます。', 'The clerk points at the card reader.')], vocab: [] },
] };

async function harness() {
  const root = await mkdtemp(path.join(os.tmpdir(), 'records-'));
  const records = createRecordStore({ root });
  const lessons = withReplays({ load: async (id) => { if (id !== lesson.id) throw Object.assign(new Error('Lesson not found'), { status: 404 }); return structuredClone(lesson); }, list: async () => [] }, records);
  const service = createAgentService({ store: lessons, llm: null, planner: null, promptIntervalMs: 0, log: async () => {}, recorder: createSessionRecorder(records) });
  return { root, records, lessons, service, cleanup: () => rm(root, { recursive: true, force: true }) };
}
const turnRequest = (overrides) => ({ sessionId: 'practice-abc', lessonId: lesson.id, beat: 0, context: {}, ...overrides });

test('JPEG sanitizer keeps pixels and drops EXIF/GPS and comments', () => {
  const { jpeg, width, height } = sanitizeJpeg(fixtureJpeg());
  assert.equal(width, 1280); assert.equal(height, 720);
  assert.ok(!jpeg.includes(Buffer.from('GPS'))); assert.ok(!jpeg.includes(Buffer.from('comment')));
  assert.ok(jpeg.includes(Buffer.from('JFIF'))); assert.ok(jpeg.includes(Buffer.from([0x12, 0x34, 0xff, 0x00, 0x56])));
  assert.throws(() => sanitizeJpeg(Buffer.from('not a jpeg')), { status: 415 });
  assert.throws(() => sanitizeJpeg(fixtureJpeg({ width: 5000 })), { status: 413 });
  assert.throws(() => sanitizeJpeg(fixtureJpeg().subarray(0, 30)), { status: 400 });
});

test('lesson turns are recorded server-side and compile into a replayable lesson', async () => {
  const h = await harness();
  try {
    const photo = await h.records.putPhoto({ userId: 'local', jpeg: Buffer.from('x'), thumb: Buffer.from('y'), width: 1, height: 1 });
    const first = await h.service.turn(turnRequest({ imageRef: { id: `photo:${photo.id}`, caption: 'My shop' }, operation: { requestId: 'r1', expectedRevision: 0 } }));
    const runId = first.operation.runId;
    await h.service.turn(turnRequest({ transcript: 'ちがう' }));                          // miss
    await h.service.turn(turnRequest({ transcript: 'こんにちは' }));                       // pass → line 2
    await h.service.turn(turnRequest({ beat: 1, intent: 'I want a sandwich' }));          // scripted detour (konbini lesson)
    await h.service.turn(turnRequest({ beat: 1, context: { gesture: 'pinch' } }));       // skip detour line 1
    await h.service.turn(turnRequest({ beat: 1, action: 'return_to_main' }));            // back to the saved line
    await h.service.turn(turnRequest({ beat: 1, context: { gesture: 'pinch' } }));       // skip main line 2

    const record = await h.records.getRecord('local', runId);
    assert.equal(record.photoId, photo.id); assert.equal(record.mode, 'practice'); assert.equal(record.lessonId, lesson.id); assert.equal(record.status, 'active');
    const summary = summarizeEvents(await h.records.events('local', runId));
    const sandwich = summary.lines.filter((l) => l.segment.kind === 'detour');
    assert.deepEqual(summary.lines.filter((l) => l.segment.kind === 'main').map((l) => l.line.jp), ['こんにちは。', 'お願いします。', 'カードで払います。']);
    assert.equal(sandwich.length, 2); assert.match(sandwich[0].line.jp, /サンドイッチ/); assert.equal(sandwich[0].skipped, true);
    assert.deepEqual(summary.lines[0].attempts.map((a) => a.score >= 75), [false, true]);
    assert.equal(summary.lines[0].passed, true); assert.equal(summary.lines[1].skipped, true);
    assert.deepEqual(summary.stats, { lines: 5, attempted: 1, passed: 1, detours: 1, bestAverage: 100 });

    const missed = await h.records.putReplay('local', (id) => compileReplay({ id, record, summary, original: lesson, deck: 'missed' }));
    assert.match(missed.id, /^rec-[a-f0-9]{12}$/); assert.equal(missed.startImage, `photos/${photo.id}`);
    assert.deepEqual(missed.beats.map((b) => [b.goal, b.lines.length]), [['Greet', 1], ['Side trip: サンドイッチを選ぶ · Choose a sandwich', 2], ['Pay', 1]]);
    assert.equal(missed.beats[0].lines[0].jp, 'お願いします。'); assert.ok(missed.beats[1].lines.every((l) => l.cue));
    assert.equal(missed.replay.sourceRecordId, runId);

    // The ordinary turn engine runs the compiled lesson, and the new run links back.
    const replayTurn = await h.service.turn({ sessionId: 'practice-replay', lessonId: missed.id, beat: 0, context: {}, imageRef: { id: `photo:${photo.id}` }, operation: { requestId: 'r2', expectedRevision: 0 } });
    assert.equal(replayTurn.overlay.jp, 'お願いします。');
    const replayRecord = await h.records.getRecord('local', replayTurn.operation.runId);
    assert.equal(replayRecord.sourceRecordId, runId); assert.equal(replayRecord.lessonId, missed.id);
    const done = await h.service.turn({ sessionId: 'practice-replay', lessonId: missed.id, beat: 0, context: {}, transcript: 'おねがいします' });
    assert.equal(done.advance, 'next_line');
    for (const [beat, l] of [[1, missed.beats[1].lines[0]], [2, missed.beats[1].lines[1]]]) assert.equal((await h.service.turn({ sessionId: 'practice-replay', lessonId: missed.id, beat, context: {}, transcript: l.kana })).advance, 'next_line');
    const end = await h.service.turn({ sessionId: 'practice-replay', lessonId: missed.id, beat: 3, context: {}, transcript: 'カードで払います' });
    assert.equal(end.advance, 'scene_done');
    assert.equal((await h.records.getRecord('local', replayTurn.operation.runId)).status, 'complete');
    assert.throws(() => compileReplay({ id: 'rec-000000000000', record, summary: { lines: summary.lines.map((l) => ({ ...l, passed: true })) }, original: lesson, deck: 'missed' }), { status: 409 });
  } finally { await h.cleanup(); }
});

test('recording failures never block a lesson turn', async () => {
  const service = createAgentService({ store: { load: async () => structuredClone(lesson) }, llm: null, planner: null, promptIntervalMs: 0, log: async () => {}, recorder: async () => { throw new Error('disk full'); } });
  const warn = console.warn; console.warn = () => {};
  try { assert.equal((await service.turn(turnRequest())).overlay.jp, 'こんにちは。'); } finally { console.warn = warn; }
});

test('HTTP photo library, session list/detail/replay/delete honor auth and ownership', async () => {
  const h = await harness();
  const app = express();
  let user = 'local';
  registerRecordRoutes(app, { store: h.records, lessons: h.lessons, authorized: (req) => req.get('portal_token') === 'fixture', userOf: () => user });
  const server = app.listen(0, '127.0.0.1'); await new Promise((resolve) => server.once('listening', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const call = (url, { method = 'GET', body } = {}) => fetch(base + url, { method, headers: { portal_token: 'fixture', ...(body ? { 'content-type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
  try {
    assert.equal((await fetch(base + '/api/photos')).status, 401);
    assert.equal((await call('/api/photos', { method: 'POST', body: { image: 'data:image/png;base64,AAAA', thumb: dataUrl(fixtureJpeg()) } })).status, 400);
    assert.equal((await call('/api/photos', { method: 'POST', body: { image: dataUrl(Buffer.from('nope')), thumb: dataUrl(fixtureJpeg()) } })).status, 415);
    const created = await call('/api/photos', { method: 'POST', body: { image: dataUrl(fixtureJpeg()), thumb: dataUrl(fixtureJpeg({ width: 320, height: 180 })) } });
    assert.equal(created.status, 201);
    const photo = await created.json();
    assert.match(photo.id, /^ph_[a-f0-9]{16}$/); assert.equal(photo.width, 1280);
    const stored = await readFile(path.join(h.root, 'photos', `${photo.id}.jpg`));
    assert.ok(!stored.includes(Buffer.from('GPS')));
    assert.equal((await (await call('/api/photos')).json()).photos.length, 1);
    const image = await call(`/api/photos/${photo.id}/thumb`);
    assert.equal(image.status, 200); assert.equal(image.headers.get('content-type'), 'image/jpeg');
    assert.equal((await call('/api/photos/../etc/image')).status, 404);

    const first = await h.service.turn(turnRequest({ imageRef: { id: `photo:${photo.id}` }, operation: { requestId: 'r1', expectedRevision: 0 } }));
    await h.service.turn(turnRequest({ transcript: 'こんにちは' }));
    const id = first.operation.runId;
    const list = await (await call('/api/sessions')).json();
    assert.equal(list.sessions.length, 1); assert.equal(list.sessions[0].stats.passed, 1);
    const detail = await (await call(`/api/sessions/${id}`)).json();
    assert.equal(detail.record.photoId, photo.id); assert.equal(detail.lines[0].attempts[0].score, 100);
    const replay = await call(`/api/sessions/${id}/replay`, { method: 'POST', body: { deck: 'all' } });
    assert.equal(replay.status, 201);
    const compiled = await replay.json();
    assert.equal((await h.lessons.load(compiled.lessonId)).beats[0].lines[0].jp, 'こんにちは。');
    assert.equal((await call(`/api/sessions/${id}/replay`, { method: 'POST', body: { deck: 'everything' } })).status, 400);

    user = 'someone-else';
    assert.equal((await call(`/api/sessions/${id}`)).status, 404);
    assert.equal((await call(`/api/photos/${photo.id}/image`)).status, 404);
    assert.equal((await (await call('/api/sessions')).json()).sessions.length, 0);
    user = 'local';

    assert.equal((await call(`/api/sessions/${id}`, { method: 'DELETE' })).status, 200);
    assert.equal((await call(`/api/sessions/${id}`)).status, 404);
    assert.equal((await call(`/api/photos/${photo.id}`, { method: 'DELETE' })).status, 200);
    assert.deepEqual(await readdir(path.join(h.root, 'photos')), []);
  } finally { server.close(); await h.cleanup(); }
});
