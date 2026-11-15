import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { snapshotReplaySource } from './compile.js';
import { withReviewVocabulary } from './review-vocabulary.js';

const demo = JSON.parse(await readFile(new URL('../../Assets/StreamingAssets/lessons/ja-konbini-demo-001.json', import.meta.url)));
const word = (jp, kana, en) => ({ jp, kana, en, objectHint: 'item' });
const entry = (targetId, jp, kana, kind = 'main') => ({ targetId, segment: { kind }, line: { jp, kana } });

test('curated saved targets inherit only vocabulary from their mapped beat and reuse verified romaji', () => {
  const record = { replaySource: snapshotReplaySource(demo) };
  const lines = demo.beats.map((beat, i) => ({ ...entry('main:' + i), line: beat.lines[0] }));
  const projected = withReviewVocabulary(record, lines);
  assert.deepEqual(projected.map(({ words }) => words.map(({ jp, romaji }) => [jp, romaji])), [
    [['サンドイッチ', 'Sandoitchi']], [['お水', 'Omizu']], [['カード', 'Kādo']],
  ]);
  for (const { line, words } of projected) for (const term of words)
    assert.equal(line.jp.slice(term.start, term.end), term.jp);
  assert.equal(lines[0].words, undefined);
});

test('saved vocabulary requires an exact lexical match and supporting kana', () => {
  const water = word('水', 'みず', 'water');
  const record = { replaySource: { version: 1, beats: [{ lineCount: 3, vocab: [water] }] } };
  const lines = [
    entry('main:0', '水曜日に行きます。', 'すいようびにいきます。'),
    entry('main:1', '水曜日に水を飲みます。', 'すいようびにみずをのみます。'),
    entry('main:2', '水をください。', 'さけをください。'),
    entry('main:1', '水をください。', 'みずをください。', 'detour'),
    entry(undefined, '水をください。', 'みずをください。'),
  ];
  const [wednesday, mixed, wrongReading, detour, legacy] = withReviewVocabulary(record, lines);
  assert.equal(wednesday.words, undefined);
  assert.deepEqual(mixed.words, [{ jp: '水', kana: 'みず', en: 'water', start: 4, end: 5 }]);
  assert.equal(wrongReading.words, undefined);
  assert.equal(detour.words, undefined);
  assert.equal(legacy.words, undefined);
  assert.deepEqual(withReviewVocabulary({}, lines), lines);
});
