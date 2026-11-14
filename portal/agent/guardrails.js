// Deliberately small, reviewable denylist; not an exhaustive copyright detector.
export const BANNED_TERMS = ['doraemon', 'ドラえもん', '哆啦a梦', '哆啦a夢', 'pokemon', 'pokémon', 'ポケモン', 'pikachu', 'ピカチュウ', 'disney', 'ディズニー', 'mickey mouse', 'ミッキーマウス', 'totoro', 'トトロ', 'ghibli', 'ジブリ', 'hello kitty', 'ハローキティ', 'naruto', 'ナルト', 'snoopy', 'スヌーピー', 'dragon ball', 'ドラゴンボール'];
const canonical = (s) => String(s).normalize('NFKC').toLowerCase().replace(/[\s\p{P}\p{Cf}]/gu, '');
export function hasBannedTerm(value) { const s = canonical(value); return BANNED_TERMS.some((term) => s.includes(canonical(term))); }
export function safePrompt(value, fallback) {
  if (typeof value !== 'string' || hasBannedTerm(value)) return fallback;
  const clean = value.replace(/[\u0000-\u001f]/g, ' ').trim();
  if (!clean) return fallback;
  const sentences = clean.match(/[^.!?。！？]+[.!?。！？]?/gu) || [clean];
  return sentences.slice(0, 3).join('').split(/\s+/u).slice(0, 95).join(' ').slice(0, 600).trim();
}
export function normalizeKana(value) {
  return String(value || '').normalize('NFKC').toLowerCase().replace(/[ァ-ヶ]/g, (c) => String.fromCharCode(c.charCodeAt(0) - 0x60)).replace(/[\s\p{P}\p{S}\p{Cf}]/gu, '');
}
function similarity(a, b) {
  if (!a || !b) return 0;
  let row = Array.from({ length: b.length + 1 }, (_, i) => i);
  for (let i = 1; i <= a.length; i++) {
    const next = [i];
    for (let j = 1; j <= b.length; j++) next[j] = Math.min(row[j] + 1, next[j - 1] + 1, row[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
    row = next;
  }
  return Math.round(100 * (1 - row[b.length] / Math.max(a.length, b.length)));
}
export function scoreTranscript(transcript, line) {
  const actual = normalizeKana(transcript);
  return Math.max(similarity(actual, normalizeKana(line.kana)), similarity(actual, normalizeKana(line.jp)));
}
