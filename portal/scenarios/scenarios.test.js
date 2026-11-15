import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createScenarioGenerator, scenarioToLesson, SCENARIO_SCHEMA } from './generate.js';
import { registerScenarioRoutes } from './routes.js';
import { createRecordStore, withReplays } from '../records/store.js';
import { createSessionRecorder } from '../records/recorder.js';
import { summarizeEvents, compileReplay } from '../records/compile.js';
import { createAgentService } from '../agent/service.js';
import { scoreTranscript } from '../agent/guardrails.js';
import { validateSchema } from '../agent/schema.js';

const u = (jp, kana, romaji = 'Romaji.', zh = '中文。', en = 'English.') => ({ jp, kana, romaji, zh, en });
const empty = u('', '', '', '', '');
const scenario = () => ({
  title: 'カフェで注文 — Ordering at a café',
  partnerRole: { ja: '店員', en: 'barista' },
  scenePrompt: 'A barista in a plain apron stands behind a wooden café counter in soft morning light. Static eye-level first-person view.',
  audioPrompt: 'Quiet café ambience with an espresso machine hissing.',
  beats: [
    { goal: 'Order a drink', lines: [
      { partner: empty, ...u('すみません、注文してもいいですか。', 'すみません、ちゅうもんしてもいいですか。'), cue: 'The barista looks up and smiles.', alternatives: [u('注文お願いします。', 'ちゅうもんおねがいします。')] },
      { partner: u('はい、何になさいますか。', 'はい、なにになさいますか。'), ...u('アイスコーヒーをください。', 'アイスコーヒーをください。'), cue: 'The barista nods and gestures toward the menu board.', alternatives: [u('アイスコーヒーを一つお願いします。', 'アイスコーヒーをひとつおねがいします。')] },
    ], vocab: [{ jp: '注文', kana: 'ちゅうもん', en: 'order', objectHint: 'Menu Board' }] },
    { goal: 'Ask for the wifi', lines: [
      { partner: u('サイズはどうなさいますか。', 'サイズはどうなさいますか。'), ...u('Mでお願いします。', 'えむでおねがいします。'), cue: 'The barista picks up a cup.', alternatives: [] },
      { partner: u('かしこまりました。', 'かしこまりました。'), ...u('Wi-Fiのパスワードを教えてください。', 'わいふぁいのぱすわーどをおしえてください。'), cue: 'The barista points to a small card on the counter.', alternatives: [] },
    ], vocab: [] },
  ],
});
const okResponse = (body) => ({ ok: true, status: 200, json: async () => ({ status: 'completed', output: [{ type: 'message', content: [{ type: 'output_text', text: JSON.stringify(body) }] }] }) });

test('model output becomes a valid lesson with partner lines and alternatives', () => {
  const lesson = scenarioToLesson(scenario(), { id: 'gen-0123456789ab', level: 'N4', request: 'order coffee', requestLanguage: 'en-US', startImage: 'photos/ph_0123456789abcdef', createdAt: 'now' });
  assert.equal(lesson.partnerRole.en, 'barista'); assert.equal(lesson.startImage, 'photos/ph_0123456789abcdef');
  assert.equal(lesson.beats[0].lines[0].partner, null);
  assert.equal(lesson.beats[0].lines[1].partner.jp, 'はい、何になさいますか。');
  assert.equal(lesson.beats[0].lines[1].alternatives.length, 1);
  assert.equal(lesson.beats[0].vocab[0].objectHint, 'menu board');
  assert.equal(lesson.scenario.request, 'order coffee');
});

test('content checks reject kanji in kana, missing partners, Latin jp and brands', () => {
  const opts = { id: 'gen-0123456789ab', level: 'N4', request: 'x', requestLanguage: '', createdAt: 'now' };
  const bad = (mutate) => { const s = scenario(); mutate(s); return () => scenarioToLesson(s, opts); };
  assert.throws(bad((s) => { s.beats[0].lines[1].kana = 'アイス珈琲をください。'; }), /kana/);
  assert.throws(bad((s) => { s.beats[0].lines[1].partner = empty; }), /partner/);
  assert.throws(bad((s) => { s.beats[1].lines[0].jp = 'M please'; }), /not Japanese/);
  assert.throws(bad((s) => { s.beats[0].lines[0].alternatives[0].romaji = 'ちゅうもん'; }), /romaji/);
  assert.throws(bad((s) => { s.beats.length = 0; }), /beats/);
  assert.throws(bad((s) => { s.scenePrompt = 'A barista in a Pikachu costume.'; }), /scene prompt/);
});

test('generator sends text only with a strict schema, retries once with the problem, then reports failure', async () => {
  const bodies = [];
  const broken = scenario(); broken.beats[0].lines[0].kana = '注文';
  const responses = [okResponse(broken), okResponse(scenario())];
  const generator = createScenarioGenerator({ apiKey: 'sk-test', model: 'gpt-6-luna', fetchImpl: async (_url, init) => { bodies.push(JSON.parse(init.body)); return responses.shift(); } });
  const lesson = await generator.generate({ request: '我想点一杯冰咖啡', level: 'N4', requestLanguage: 'zh-CN', startImage: 'photos/ph_0123456789abcdef' });
  assert.match(lesson.id, /^gen-[a-f0-9]{12}$/);
  assert.equal(bodies.length, 2);
  assert.deepEqual(JSON.parse(bodies[0].input), { request: '我想点一杯冰咖啡', level: 'N4', hasPhotoBackdrop: true });
  assert.match(JSON.parse(bodies[1].input).previousAttemptRejected, /kana/);
  assert.equal(bodies[0].text.format.strict, true); assert.equal(bodies[0].store, false);
  assert.ok(!JSON.stringify(bodies).includes('ph_0123456789abcdef'), 'photo ids/images never reach the model');

  const always = createScenarioGenerator({ apiKey: 'sk-test', fetchImpl: async () => okResponse(broken) });
  await assert.rejects(always.generate({ request: 'coffee', level: 'N4' }), { status: 502 });
  await assert.rejects(createScenarioGenerator({ apiKey: '' }).generate({ request: 'coffee', level: 'N4' }), { status: 503 });
  const busy = createScenarioGenerator({ apiKey: 'sk-test', fetchImpl: async () => ({ ok: false, status: 429 }) });
  await assert.rejects(busy.generate({ request: 'coffee', level: 'N4' }), { status: 429 });
  assert.ok(SCENARIO_SCHEMA.properties.beats.items.properties.lines.items.properties.alternatives);
});

test('alternative proposed answers score as correct replies', () => {
  const line = scenarioToLesson(scenario(), { id: 'gen-0123456789ab', level: 'N4', request: 'x', createdAt: 'now' }).beats[0].lines[1];
  assert.equal(scoreTranscript('アイスコーヒーを一つお願いします', line), 100);
  assert.equal(scoreTranscript('アイスコーヒーをください', line), 100);
  assert.ok(scoreTranscript('トイレはどこですか', line) < 75);
});

test('routes create, list, run, replay and delete a generated scenario', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'scenarios-'));
  const records = createRecordStore({ root });
  const lessons = withReplays({ load: async () => { throw Object.assign(new Error('Lesson not found'), { status: 404 }); } }, records);
  const service = createAgentService({ store: lessons, llm: null, planner: null, promptIntervalMs: 0, log: async () => {}, recorder: createSessionRecorder(records) });
  const photo = await records.putPhoto({ userId: 'local', jpeg: Buffer.from('x'), thumb: Buffer.from('y'), width: 1, height: 1 });
  const app = express(); app.use(express.json());
  registerScenarioRoutes(app, { store: records, generator: createScenarioGenerator({ apiKey: 'sk-test', fetchImpl: async () => okResponse(scenario()) }), authorized: (req) => req.get('portal_token') === 'fixture' });
  const server = app.listen(0, '127.0.0.1'); await new Promise((resolve) => server.once('listening', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const call = (url, { method = 'GET', body } = {}) => fetch(base + url, { method, headers: { portal_token: 'fixture', 'content-type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
  try {
    assert.equal((await fetch(base + '/api/scenarios', { method: 'POST' })).status, 401);
    assert.equal((await call('/api/scenarios', { method: 'POST', body: { request: 'hi', level: 'N4' } })).status, 400);
    assert.equal((await call('/api/scenarios', { method: 'POST', body: { request: 'order coffee', level: 'N9' } })).status, 400);
    assert.equal((await call('/api/scenarios', { method: 'POST', body: { request: 'order coffee', level: 'N4', image: 'photo:ph_ffffffffffffffff' } })).status, 404);
    assert.equal((await call('/api/scenarios', { method: 'POST', body: { request: 'order coffee', level: 'N4', image: 'http://evil' } })).status, 400);
    const created = await call('/api/scenarios', { method: 'POST', body: { request: 'order an iced coffee and ask for the wifi', level: 'N4', image: `photo:${photo.id}`, language: 'en-US' } });
    assert.equal(created.status, 201);
    const lesson = await created.json();
    assert.equal(lesson.startImage, `photos/${photo.id}`);
    assert.equal((await (await call('/api/scenarios')).json()).scenarios[0].partnerRole.en, 'barista');
    assert.equal((await lessons.load(lesson.id)).title, lesson.title);

    // The generated lesson runs through the ordinary turn engine.
    const req = (o) => ({ sessionId: 'practice-gen', lessonId: lesson.id, beat: 0, context: {}, ...o });
    const first = await service.turn(req({ imageRef: { id: `photo:${photo.id}` }, operation: { requestId: 'g1', expectedRevision: 0 } }));
    assert.equal(first.overlay.jp, 'すみません、注文してもいいですか。'); assert.equal(first.speech, null, 'the learner opens this exchange');
    const second = await service.turn(req({ transcript: '注文お願いします' }));
    assert.equal(second.advance, 'next_line'); assert.equal(second.speech.text, 'はい、何になさいますか。'); assert.ok(validateSchema(second));
    const wrong = await service.turn(req({ beat: 1, transcript: 'トイレはどこですか' }));
    assert.equal(wrong.advance, 'stay'); assert.equal(wrong.speech, null);

    const record = await records.getRecord('local', first.operation.runId);
    const summary = summarizeEvents(await records.events('local', record.id));
    const replay = compileReplay({ id: 'rec-0123456789ab', record, summary, original: lesson, deck: 'missed' });
    assert.equal(replay.partnerRole.en, 'barista');
    assert.equal(replay.beats[0].lines[0].partner.jp, 'はい、何になさいますか。');
    assert.equal(replay.beats[0].lines[0].alternatives.length, 1);

    assert.equal((await call(`/api/scenarios/${lesson.id}`, { method: 'DELETE' })).status, 200);
    assert.equal((await (await call('/api/scenarios')).json()).scenarios.length, 0);
  } finally { server.close(); await rm(root, { recursive: true, force: true }); }
});
