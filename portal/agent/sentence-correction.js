import { normalizeKana } from './guardrails.js';
import { readingGroupsFor } from './reading-groups.js';

function matchingBoundary(text, units, fromEnd = false) {
  let offsets = [fromEnd ? text.length : 0];
  for (const { forms } of units) {
    offsets = [...new Set(offsets.flatMap((offset) => forms.flatMap((form) => {
      const matches = fromEnd ? text.endsWith(form, offset) : text.startsWith(form, offset);
      return matches ? [offset + (fromEnd ? -form.length : form.length)] : [];
    })))];
    if (!offsets.length) break;
  }
  return offsets;
}

/** Locate a single short unmatched token between exact reviewed lexical units. */
export function sentenceCorrection(transcript, target) {
  const groups = readingGroupsFor(target);
  if (!groups) return undefined;
  const heard = normalizeKana(transcript);
  if (!/^[\p{Script=Hiragana}\p{Script=Han}ー]{4,80}$/u.test(heard)) return undefined;

  let offset = 0;
  const units = groups.map(({ jp, kana }) => {
    const start = offset;
    offset += jp.length;
    const word = jp.replace(/[\p{P}\s]+$/u, '');
    return { start, end: start + word.length, jp: word, forms: [...new Set([normalizeKana(jp), normalizeKana(kana)])] };
  });
  if (matchingBoundary(heard, units).includes(heard.length)) return undefined;
  const candidates = units.filter(({ forms }, index) => {
    const left = matchingBoundary(heard, units.slice(0, index));
    const right = matchingBoundary(heard, units.slice(index + 1).reverse(), true);
    return left.some((from) => right.some((to) => {
      if (from > to || heard.length - (to - from) < 4) return false;
      const differing = heard.slice(from, to);
      // Incomplete starts and endings leave little evidence about the omitted edge.
      if (!differing) return index > 0 && index < units.length - 1;
      // Additional material does not establish that the expected group was wrong.
      if (forms.some((form) => differing.includes(form))) return false;
      return forms.some((form) => Math.abs(form.length - differing.length) <= 1)
        && (forms.some((form) => form.length > 1) || differing.length === 1);
    }));
  });
  if (candidates.length !== 1) return undefined;
  const [{ start, end, jp }] = candidates;
  return { start, end, jp };
}
