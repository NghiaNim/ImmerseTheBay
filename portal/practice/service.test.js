import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { createLessonStore } from '../agent/lessons.js';
import { createPracticeService, buildPracticeLesson, parsePracticeInput, PRACTICE_SCHEMA } from './service.js';
import { registerPracticeRoutes } from './routes.js';
const photo = 'data:image/png;base64,iVBORw0KGgo=';
const line = { jp: '水をください。', kana: 'みずをください。', romaji: 'Mizu o kudasai.', en: 'Water, please.', zh: '请给我水。', cue: 'Reflections drift along the counter.' };
const partner = { jp: 'いらっしゃいませ。', kana: 'いらっしゃいませ。', romaji: 'Irasshaimase.', en: 'Welcome.', zh: '欢迎光临。' };
const opener = { jp: '', kana: '', romaji: '', en: '', zh: '' };
const output = { visiblePartner: false, partnerRole: { ja: '店員', en: 'shop clerk' }, title: 'Ask for water', scenePrompt: 'The photographed room remains stationary at eye level.', audioPrompt: 'Quiet room ambience.', goal: 'Ask for water politely', lines: [{ ...line, partner: opener }, { ...line, jp: '一つお願いします。', kana: 'ひとつおねがいします。', romaji: 'Hitotsu onegaishimasu.', en: 'One, please.', zh: '请给我一个。', cue: 'Steam rises above the existing cup.', partner: { ...partner, jp: 'いくつですか。', kana: 'いくつですか。', romaji: 'Ikutsu desu ka.', en: 'How many?', zh: '几个？' } }, { ...line, jp: 'ありがとうございます。', kana: 'ありがとうございます。', romaji: 'Arigatou gozaimasu.', en: 'Thank you.', zh: '谢谢。', cue: 'Leaves stir near the existing window.', partner: { ...partner, jp: 'どうぞ。', kana: 'どうぞ。', romaji: 'Douzo.', en: 'Here you are.', zh: '请。' } }], vocab: [{ jp: '水', kana: 'みず', en: 'water', objectHint: 'water' }, { jp: '一つ', kana: 'ひとつ', en: 'one', objectHint: 'bottle' }] };
const generated = (candidate = output) => ({ ok: true, json: async () => ({ status: 'completed', output: [{ type: 'message', content: [{ type: 'output_text', text: JSON.stringify(candidate) }] }] }) });
test('real image and goal are separate structured multimodal inputs; lesson remains photo-free', async () => {
  const store = createLessonStore(); let request;
  const service = createPracticeService({ store, apiKey: 'test', fetchImpl: async (_url, req) => { request = JSON.parse(req.body); return generated(); } });
  const result = await service.generate({ image: photo, goal: 'Ask for water', visualPrompt: 'Show the cup steaming' });
  assert.equal(JSON.parse(request.input[0].content[0].text).visualPrompt, 'Show the cup steaming');
  assert.equal(request.input[0].content[1].image_url, photo);
  assert.equal(request.model, 'gpt-6-luna'); assert.equal(request.store, false);
  assert.equal(result.lesson.beats[0].lines.length, 3);
  assert.equal(result.lesson.visiblePartner, false);
  assert.equal(request.text.format.schema.properties.visiblePartner.type, 'boolean');
  assert.ok(request.text.format.schema.required.includes('visiblePartner'));
  assert.equal(JSON.stringify(result.lesson).includes('base64'), false);
  assert.deepEqual(await store.load(result.lesson.id), result.lesson);
  assert.equal(store.ephemeralImage(result.lesson.id).mimeType, 'image/png');
  assert.ok(!(await store.list()).some(l => l.id === result.lesson.id));
});
test('ephemeral image and lesson share TTL and bounded eviction, load copies cannot mutate store', async () => {
  let now = 0; const store = createLessonStore(undefined, { now: () => now, ttlMs: 1000, maxEphemeral: 1 });
  const first = buildPracticeLesson(output), second = buildPracticeLesson(output);
  const image = { bytes: Buffer.from('test'), mimeType: 'image/png' };
  store.addEphemeral(first, image); const copy = await store.load(first.id); copy.title = 'mutated';
  assert.equal((await store.load(first.id)).title, first.title);
  store.addEphemeral(second, image); await assert.rejects(store.load(first.id), { status: 404 });
  assert.throws(() => store.ephemeralImage(first.id), { status: 404 });
  now = 1001; await assert.rejects(store.load(second.id), { status: 404 });
  assert.throws(() => store.ephemeralImage(second.id), { status: 404 });
});
test('input and provider output bounds reject malformed, excessive and incomplete data', async () => {
  for (const body of [{ image: 'https://example.test', goal: 'hello' }, { image: photo, goal: 'x'.repeat(501) }, { image: photo, goal: 'hello', extra: true }, { image: 'data:image/jpeg;base64,iVBORw0KGgo=', goal: 'hello' }]) assert.throws(() => parsePracticeInput(body));
  assert.throws(() => buildPracticeLesson({ ...output, visiblePartner: 'false' }), { status: 502 });
  assert.throws(() => buildPracticeLesson({ ...output, visiblePartner: undefined }), { status: 502 });
  assert.throws(() => buildPracticeLesson({ ...output, lines: [line] }), { status: 502 });
  assert.throws(() => buildPracticeLesson({ ...output, partnerRole: { ja: '', en: 'clerk' } }), { status: 502 });
  assert.throws(() => buildPracticeLesson({ ...output, lines: [{ ...line, partner: opener }, { ...line, partner: opener }, output.lines[2]] }), { status: 502 });
  const built = buildPracticeLesson(output).beats[0].lines;
  assert.equal(built[0].partner, null); assert.equal(built[1].partner.jp, output.lines[1].partner.jp);
  assert.throws(() => buildPracticeLesson({ ...output, lines: [{ ...line, romaji: '', partner: opener }, { ...line, partner }, output.lines[2]] }), { status: 502 });
  const service = createPracticeService({ store: createLessonStore(), apiKey: 'test', fetchImpl: async () => ({ ok: false }) });
  await assert.rejects(service.generate({ image: photo, goal: 'hello' }), { status: 502 });
  await assert.rejects(service.generate({ image: photo, goal: 'hello' }), { status: 429 });
});
test('a nonconforming photo lesson is regenerated once with tighter bounds', async () => {
  const requests = [];
  const service = createPracticeService({ store: createLessonStore(), apiKey: 'test', fetchImpl: async (_url, req) => {
    requests.push(req);
    if (requests.length > 1) return generated();
    return { ok: true, json: async () => ({ status: 'completed', output: [{ type: 'message', content: [
      { type: 'output_text', text: JSON.stringify({ ...output, scenePrompt: 'x'.repeat(601) }) },
    ] }] }) };
  } });
  const { lesson } = await service.generate({ image: photo, goal: 'Ask for water' });
  assert.equal(lesson.title, output.title);
  assert.equal(requests.length, 2);
  assert.equal(requests[0].signal, requests[1].signal);
  assert.match(JSON.parse(requests[1].body).instructions, /Use exactly three short learner exchanges/);
});
test('route authenticates before large parser and serves transient photo without caching', async t => {
  const store = createLessonStore(); const service = createPracticeService({ store, apiKey: 'test', fetchImpl: async () => generated() });
  const app = express(); registerPracticeRoutes(app, { store, service, authorized: req => req.get('portal_token') === 'test' });
  app.use(express.json({ limit: '64kb' }));
  const server = app.listen(0, '127.0.0.1'); await new Promise(r => server.once('listening', r)); t.after(() => server.close());
  const url = `http://127.0.0.1:${server.address().port}`;
  const denied = await fetch(url + '/api/practice/generate', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ image: photo, goal: 'hello' }) }); assert.equal(denied.status, 401);
  const response = await fetch(url + '/api/practice/generate', { method: 'POST', headers: { 'content-type': 'application/json', portal_token: 'test' }, body: JSON.stringify({ image: photo, goal: 'hello' }) });
  assert.equal(response.status, 200); const { lesson } = await response.json();
  const image = await fetch(url + lesson.startImage, { headers: { portal_token: 'test' } }); assert.equal(image.status, 200); assert.equal(image.headers.get('cache-control'), 'no-store');
});
test('generated partner lines drive lesson speech and the live voice opening', async () => {
  const { createAgentService } = await import('../agent/service.js');
  const lesson = buildPracticeLesson({ ...output, lines: [{ ...line, partner }, ...output.lines.slice(1)] });
  const service = createAgentService({ store: { load: async () => structuredClone(lesson) }, llm: null, planner: null, promptIntervalMs: 0, log: async () => {} });
  const first = await service.turn({ sessionId: 'partner-test', lessonId: lesson.id, beat: 0, context: {} });
  assert.equal(first.speech.text, partner.jp);
  const context = service.snapshot({ sessionId: 'partner-test', lessonId: lesson.id }).lessonContext;
  assert.equal(context.partnerRole, '店員 / shop clerk');
  assert.equal(context.partnerLine, partner.jp);
  for (const [beat, line] of lesson.beats[0].lines.entries()) {
    const response = await service.turn({ sessionId: 'partner-test', lessonId: lesson.id, beat, context: {}, transcript: line.jp });
    assert.equal(response.advance, beat === 2 ? 'scene_done' : 'next_line');
    assert.equal(response.overlay?.jp || null, lesson.beats[0].lines[beat + 1]?.jp || null);
  }
});

test('photo generation uses the shared three-turn profile, defaults to N5, and respects explicit levels', async () => {
  for (const level of [undefined, 'N4', 'N1']) {
    let request;
    const service = createPracticeService({ store: createLessonStore(), apiKey: 'test', fetchImpl: async (_url, init) => { request = JSON.parse(init.body); return generated(); } });
    const { lesson } = await service.generate({ image: photo, goal: 'Ask for water', ...(level ? { level } : {}) });
    assert.equal(lesson.level, level || 'N5');
    assert.equal(JSON.parse(request.input[0].content[0].text).level, level || 'N5');
    assert.deepEqual(lesson.generationProfile, { id: 'demo-three-steps', turns: 3 });
    assert.equal(lesson.beats.flatMap(b => b.lines).length, 3);
    assert.equal(lesson.beats[0].lines[2].jp, 'ありがとうございます。');
    assert.equal(request.text.format.schema.properties.lines.minItems, 3);
    assert.equal(request.text.format.schema.properties.lines.maxItems, 3);
    assert.match(request.instructions, /one primary learning intent/);
  }
  for (const level of ['N9', '', null]) assert.throws(() => parsePracticeInput({ image: photo, goal: 'water', level }), { status: 400 });
  assert.equal(PRACTICE_SCHEMA.properties.lines.items.properties.jp.maxLength, 40);
});

test('photo rejects two/four turns and long speech within one retry without storing invalid lessons', async () => {
  const mutations = [
    s => s.lines.pop(),
    s => s.lines.push(structuredClone(s.lines[2])),
    s => { s.lines[0].jp = '水'.repeat(41); },
    s => { s.lines[1].partner.kana = 'みず'.repeat(31); },
    s => { s.lines[0].jp = '水をください。お茶もください。'; },
    s => { s.vocab.pop(); },
  ];
  for (const mutate of mutations) {
    const broken = structuredClone(output); mutate(broken);
    let calls = 0, stored = 0;
    const requests = [];
    const service = createPracticeService({ store: { addEphemeral: () => { stored++; return 123; } }, apiKey: 'test', fetchImpl: async (_url, init) => { requests.push(JSON.parse(init.body)); return generated(++calls === 1 ? broken : output); } });
    const { lesson } = await service.generate({ image: photo, goal: 'water' });
    assert.equal(calls, 2); assert.equal(stored, 1); assert.equal(lesson.beats[0].lines.length, 3);
    assert.match(requests[1].instructions, /Use exactly three short learner exchanges/);
    calls = 0; stored = 0;
    const rejected = createPracticeService({ store: { addEphemeral: () => { stored++; } }, apiKey: 'test', fetchImpl: async () => { calls++; return generated(broken); } });
    await assert.rejects(rejected.generate({ image: photo, goal: 'water' }), { status: 502 });
    assert.equal(calls, 2); assert.equal(stored, 0);
  }
});

test('photo casting is scoped to the conversation partner and allows explicit voice override', () => {
  assert.equal(buildPracticeLesson({ ...output, voiceRecommendation: 'male' }).partnerVoice, 'female');
  assert.equal(buildPracticeLesson({ ...output, visiblePartner: true, voiceRecommendation: 'male' }).partnerVoice, 'male');
  assert.equal(buildPracticeLesson({ ...output, visiblePartner: true, voiceRecommendation: 'unknown' }).partnerVoice, 'female');
  assert.equal(buildPracticeLesson(output, undefined, { partnerVoice: 'male' }).partnerVoice, 'male');
  assert.throws(() => parsePracticeInput({ image: photo, goal: 'Water please', partnerVoice: 'provider-id' }), { status: 400 });
});
