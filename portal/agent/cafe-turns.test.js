import assert from 'node:assert/strict';
import test from 'node:test';
import { createLessonStore, validateLesson } from './lessons.js';
import { RESPONSE_SCHEMA, WIRE_RESPONSE_SCHEMA, validateSchema } from './schema.js';
import { createAgentService } from './service.js';

const store = createLessonStore();
const lessonId = 'ja-cafe-demo-001';
const sessionId = 'cafe-conversation';
const makeService = options => createAgentService({
  llm: null, planner: null, log: async () => {}, promptIntervalMs: 0, ...options,
});
const request = (requestId, beat, previous, extra = {}) => ({
  sessionId, lessonId, beat, context: {},
  operation: {
    requestId, expectedRevision: previous?.operation.revision || 0,
    ...(previous ? { runId: previous.operation.runId } : {}),
  },
  ...extra,
});
const attempt = transcript => ({ transcript, practiceAttempt: true });
function assertClerkLine(value, text, english) {
  assert.equal(value.role, 'clerk');
  assert.equal(value.language, 'ja');
  assert.equal(value.text, text);
  assert.equal(value.english, english);
  assert.ok(value.romaji?.length);
}

test('coffee asks before the learner, reacts to retry and passed lines, and closes after final thanks', async () => {
  const service = makeService();
  const open = await service.turn(request('open', 0));
  assertClerkLine(open.speech, 'いらっしゃいませ。ご注文は？', 'Welcome. What would you like?');
  assert.equal(open.reaction, undefined);
  const failed = await service.turn(request('retry', 0, open, attempt('席はどこですか。')));
  assert.equal(failed.attempt.outcome, 'retry');
  assert.equal(failed.speech, null);
  assert.deepEqual(failed.visko, []);
  assertClerkLine(failed.reaction, 'すみません。もう一度お願いします。', 'Sorry. Could you say that again?');
  assert.equal(failed.overlay.jp, open.overlay.jp);
  const passRequest = request('coffee', 0, failed, attempt(open.overlay.jp));
  const passed = await service.turn(passRequest);
  assert.equal(passed.attempt.outcome, 'pass');
  assertClerkLine(passed.reaction, 'かしこまりました。', 'Certainly.');
  assertClerkLine(passed.speech, 'ミルクは入れますか。', 'Would you like milk?');
  assert.equal(passed.scene.id, 'cafe-menupour');
  const replay = await service.turn(passRequest);
  assert.deepEqual(replay, { ...passed, operation: { ...passed.operation, replayed: true } });
  const milk = await service.turn(request('milk', 1, passed, attempt(passed.overlay.jp)));
  assertClerkLine(milk.reaction, 'はい、ミルクですね。', 'Milk, of course.');
  assertClerkLine(milk.speech, 'お待たせしました。どうぞ。', 'Thanks for waiting. Here you go.');
  assert.equal(milk.scene.id, 'cafe-serve');
  const done = await service.turn(request('thanks', 2, milk, attempt(milk.overlay.jp)));
  assert.equal(done.advance, 'scene_done');
  assert.equal(done.speech, null);
  assert.equal(done.overlay, null);
  assertClerkLine(done.reaction, 'ありがとうございます。またどうぞ。', 'Thank you. Come again.');
  assert.deepEqual(service.snapshot({ sessionId, lessonId }).response.reaction, done.reaction);
  for (const decision of [open, failed, passed, replay, milk, done]) {
    assert.equal(validateSchema(decision, WIRE_RESPONSE_SCHEMA), true);
  }
  assert.equal('reaction' in RESPONSE_SCHEMA.properties, false);
});

test('unscored coffee actions have no reaction and detours do not inherit the saved order response', async () => {
  const lesson = await store.load(lessonId);
  const sideLine = { ...lesson.beats[1].lines[0] };
  delete sideLine.partner;
  delete sideLine.reaction;
  const service = makeService({ planner: async ({ returnBeat }) => ({
    label: 'Ask about milk', returnBeat, nodes: [sideLine],
  }) });
  const open = await service.turn(request('open', 0));
  const idle = await service.turn(request('idle', 0, open, { action: 'idle_hint' }));
  assertClerkLine(idle.speech, 'お決まりですか。', 'Have you decided?');
  assert.equal(idle.reaction, undefined);
  const detour = await service.turn(request('detour', 0, idle, { intent: 'Ask what is in the milk pitcher' }));
  assert.equal(detour.route.kind, 'detour');
  assert.equal(detour.reaction, undefined);
  const branched = await service.turn(request('answer', 0, detour, attempt(detour.overlay.jp)));
  assert.equal(branched.route.kind, 'rejoin');
  assertClerkLine(branched.reaction, 'はい、わかりました。', 'Yes, I understand.');
  assert.deepEqual(branched.speech, open.speech);
  const skip = await service.turn(request('skip', 0, branched, { context: { gesture: 'pinch' } }));
  assert.equal(skip.attempt.outcome, 'skip');
  assert.equal(skip.reaction, undefined);
});

test('authored reactions require exact bilingual line data', async () => {
  const lesson = await store.load(lessonId);
  const target = lesson.beats[0].lines[0];
  for (const bad of [null, 'incorrect', { jp: 'はい。', romaji: 'Hai.' }, { ...target.reaction, english: 'extra' }]) {
    const invalid = structuredClone(lesson);
    invalid.beats[0].lines[0].reaction = bad;
    assert.throws(() => validateLesson(invalid), { status: 422, message: 'Invalid lesson reaction' });
  }
});
