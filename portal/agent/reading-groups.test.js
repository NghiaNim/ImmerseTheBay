import test from 'node:test';
import assert from 'node:assert/strict';
import { readingGroupsFor } from './reading-groups.js';
import { validateSchema, WIRE_RESPONSE_SCHEMA } from './schema.js';
import { createAgentService } from './service.js';

const demoLines = [
  {
    jp: 'サンドイッチはどこですか。', kana: 'さんどいっちはどこですか。',
    romaji: 'Sandoitchi wa doko desu ka?', en: 'Where are the sandwiches?', zh: '三明治在哪里？',
  },
  {
    jp: 'お水をください。', kana: 'おみずをください。',
    romaji: 'Omizu o kudasai.', en: 'Water, please.', zh: '请给我水。',
  },
  {
    jp: 'カードで払えますか。', kana: 'かーどではらえますか。',
    romaji: 'Kādo de haraemasu ka?', en: 'Can I pay by card?', zh: '可以刷卡吗？',
  },
];

test('reviewed demo groups preserve each full utterance and contain its particles', () => {
  for (const line of demoLines) {
    const groups = readingGroupsFor(line);
    assert.ok(groups.length >= 3);
    assert.equal(groups.map(({ jp }) => jp).join(''), line.jp);
    assert.equal(groups.map(({ romaji }) => romaji).join(' '), line.romaji);
  }
  assert.deepEqual(readingGroupsFor(demoLines[0])[1], { jp: 'は', kana: 'は', romaji: 'wa' });
  assert.deepEqual(readingGroupsFor(demoLines[1])[0], { jp: 'お水', kana: 'おみず', romaji: 'Omizu' });
  assert.deepEqual(readingGroupsFor(demoLines[1])[1], { jp: 'を', kana: 'を', romaji: 'o' });
  assert.deepEqual(readingGroupsFor({ ...demoLines[2], kana: 'カードではらえますか。' }), readingGroupsFor(demoLines[2]));
});

test('changed readings, punctuation and unknown targets do not get guessed groups', () => {
  for (const change of [{ kana: 'おすいをください。' }, { romaji: 'Omizu wo kudasai.' }, { jp: 'お水をください' }, { romaji: '' }])
    assert.equal(readingGroupsFor({ ...demoLines[1], ...change }), undefined);
  const groups = readingGroupsFor(demoLines[1]);
  groups[0].romaji = 'Tampered';
  assert.equal(readingGroupsFor(demoLines[1])[0].romaji, 'Omizu');
});

test('ordinary lesson turns carry optional verified groups only in the wire response', async () => {
  const unknown = { jp: '袋はいりません。', kana: 'ふくろはいりません。', romaji: 'Fukuro wa irimasen.', en: 'No bag, thanks.', zh: '不需要袋子。' };
  const lines = [...demoLines, unknown].map((line) => ({ ...line, cue: 'The clerk waits beside the counter.' }));
  const lesson = {
    id: 'demo-reading-groups', title: 'Shop practice', level: 'N5',
    scenePrompt: 'A clerk stands by the counter.', audioPrompt: 'Quiet room tone.', startImage: '',
    beats: [{ goal: 'Ask for an item', vocab: [], lines }],
  };
  let modelFallback;
  const service = createAgentService({ store: { load: async () => lesson }, llm: async ({ fallback }) => {
    modelFallback = fallback;
    return fallback;
  }, planner: null, promptIntervalMs: 0, log: async () => {} });

  for (const [index, expected] of lines.entries()) {
    const response = await service.turn({
      sessionId: 'test-words', lessonId: lesson.id, beat: Math.max(0, index - 1),
      ...(index > 0 ? { transcript: lines[index - 1].jp } : {}), context: {},
    });
    assert.equal(response.overlay.jp, expected.jp);
    assert.equal(response.overlay.romaji, expected.romaji);
    assert.equal(modelFallback.overlay.readingGroups, undefined);
    assert.deepEqual(response.overlay.readingGroups, readingGroupsFor(expected));
    assert.ok(validateSchema(response, WIRE_RESPONSE_SCHEMA));
    if (index > 0) assert.equal(response.feedback.score, 100);
  }
});
