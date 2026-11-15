import test from 'node:test';
import assert from 'node:assert/strict';
import { sentenceCorrection } from './sentence-correction.js';
import { createAgentService } from './service.js';
import { validateSchema, WIRE_RESPONSE_SCHEMA } from './schema.js';

const water = { jp: 'お水をください。', kana: 'おみずをください。', romaji: 'Omizu o kudasai.' };
const card = { jp: 'カードで払えますか。', kana: 'かーどではらえますか。', romaji: 'Kādo de haraemasu ka?' };
const utterance = { ...water, en: 'Water, please.', zh: '请给我水。', cue: 'The water stays in place.' };

test('one reviewed difference identifies the exact displayed UTF-16 substring', () => {
  for (const transcript of ['お水がください。', 'おみずがください', 'おみず、が ください！']) {
    assert.deepEqual(sentenceCorrection(transcript, water), { start: 2, end: 3, jp: 'を' });
  }
  assert.deepEqual(sentenceCorrection('ｶｰﾄﾞに払えますか？', card), { start: 3, end: 4, jp: 'で' });
});

test('an exact transcript, unrelated text, ambiguity and unreviewed targets have no localized suggestion', () => {
  for (const transcript of ['', 'こんにちは', 'お水をください。', 'おみずをください', 'ください',
    'お水を', 'お茶がください', 'お水ををください', '水ください', 'お水𠮷をください']) {
    assert.equal(sentenceCorrection(transcript, water), undefined, transcript);
  }
  assert.equal(sentenceCorrection('お水がください', { ...water, romaji: 'Wrong reading' }), undefined);
  assert.equal(sentenceCorrection('こんばんはは', { jp: 'こんにちはは', kana: 'こんにちはは', romaji: 'Konnichiwa wa' }), undefined);
});

test('committed attempt carries a correction through replay and keeps an accepted alternative clean', async () => {
  const alternative = { ...utterance, jp: 'お水をお願いします。', kana: 'おみずをおねがいします。', romaji: 'Omizu o onegaishimasu.' };
  const lesson = {
    id: 'ja-test-water', title: 'Order water', scenePrompt: 'The same grocery refrigerator.',
    audioPrompt: 'A quiet refrigerator.', beats: [{ goal: 'Ask for water', vocab: [], lines: [
      { ...utterance, alternatives: [alternative] },
    ] }],
  };
  const service = createAgentService({ store: { load: async () => lesson }, llm: null, planner: null, log: async () => {}, promptIntervalMs: 0 });
  const request = (id, response, transcript = '') => ({
    sessionId: 'correction-test', lessonId: lesson.id, beat: 0, transcript, context: {},
    operation: { requestId: id, expectedRevision: response?.operation.revision ?? 0,
      ...(response ? { runId: response.operation.runId } : {}) },
  });
  const opened = await service.turn(request('open'));
  const retryRequest = request('retry', opened, 'おみずがください');
  const retry = await service.turn(retryRequest);
  assert.equal(retry.attempt.outcome, 'retry');
  assert.deepEqual(retry.attempt.correction, { start: 2, end: 3, jp: 'を' });
  assert.deepEqual((await service.turn(retryRequest)).attempt, retry.attempt);
  assert.ok(validateSchema(retry, WIRE_RESPONSE_SCHEMA));
  const passed = await service.turn(request('alternative', retry, alternative.jp));
  assert.equal(passed.attempt.outcome, 'pass');
  assert.equal(passed.attempt.correction, undefined);
  assert.equal(passed.advance, 'scene_done');
});
