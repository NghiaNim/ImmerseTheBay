import { normalizeKana } from '../agent/guardrails.js';
import { readingGroupsFor } from '../agent/reading-groups.js';

const segmenter = new Intl.Segmenter('ja', { granularity: 'word' });

function occurrences(line) {
  const groups = readingGroupsFor(line);
  if (groups) {
    let start = 0;
    return groups.map((group) => {
      const end = start + group.jp.length;
      const entry = { ...group, start, end };
      start = end;
      return entry;
    });
  }
  return Array.from(segmenter.segment(line.jp)).flatMap(({ segment, index, isWordLike }) =>
    isWordLike ? [{ jp: segment, start: index, end: index + segment.length }] : []);
}

export function withReviewVocabulary(record, lines) {
  const source = record.replaySource;
  if (source?.version !== 1) return lines;
  const vocabularies = source.beats.flatMap((beat) => Array.from({ length: beat.lineCount }, () => beat.vocab));
  return lines.map((entry) => {
    const main = entry.segment?.kind === 'main' && /^main:(\d+)$/.exec(entry.targetId);
    const vocab = main && vocabularies[Number(main[1])];
    const line = entry.line;
    if (!vocab?.length || !line?.jp || !line.kana) return entry;

    const sentenceKana = normalizeKana(line.kana);
    const words = occurrences(line).flatMap((part) => {
      const matches = new Map();
      for (const { jp, kana, en } of vocab) {
        const reading = normalizeKana(kana);
        if (jp !== part.jp || !reading || !en ||
            !(part.kana ? reading === normalizeKana(part.kana) : sentenceKana.includes(reading))) continue;
        matches.set(JSON.stringify([reading, en]), { jp, kana, en, start: part.start, end: part.end,
          ...(part.romaji ? { romaji: part.romaji } : {}) });
      }
      // Competing dictionary entries offer no reliable reading or gloss for this occurrence.
      return matches.size === 1 ? [...matches.values()] : [];
    });
    return words.length ? { ...entry, words } : entry;
  });
}
