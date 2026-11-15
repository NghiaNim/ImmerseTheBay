// Deliberately small, reviewable denylist; not an exhaustive copyright detector.
export const BANNED_TERMS = ['doraemon', 'ドラえもん', '哆啦a梦', '哆啦a夢', 'pokemon', 'pokémon', 'ポケモン', 'pikachu', 'ピカチュウ', 'disney', 'ディズニー', 'mickey mouse', 'ミッキーマウス', 'totoro', 'トトロ', 'ghibli', 'ジブリ', 'hello kitty', 'ハローキティ', 'naruto', 'ナルト', 'snoopy', 'スヌーピー', 'dragon ball', 'ドラゴンボール', 'anpanman', 'アンパンマン', '面包超人', '麵包超人', 'sailor moon', 'セーラームーン', '美少女战士', '美少女戦士', '美少女戰士', '皮卡丘', '標準日本語', '标准日本语'];
const canonical = (s) => String(s).normalize('NFKC').toLowerCase().replace(/\p{Cf}/gu, '');
const escapeRegex = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const patterns = BANNED_TERMS.map((term) => {
  const normalized = canonical(term);
  // Keep word boundaries: stripping spaces turns ordinary romaji "naru to"
  // into a character name. Still catch fullwidth/zero-width/punctuation disguises.
  if (/^[\p{Script=Latin} ]+$/u.test(normalized)) {
    const words = normalized.split(' ').map((word) => [...word].map(escapeRegex).join('[\\p{P}]*'));
    return new RegExp('(?<![\\p{L}\\p{N}])' + words.join('[\\s\\p{P}]+') + '(?![\\p{L}\\p{N}])', 'u');
  }
  // Kana/CJK names have no word spaces, so a space or punctuation inside one is a disguise.
  return new RegExp([...normalized].map(escapeRegex).join('[\\s\\p{P}]*'), 'u');
});
export function hasBannedTerm(value) { const s = canonical(value); return patterns.some((pattern) => pattern.test(s)); }
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
// One swapped particle is a small edit but a grammar error (ひきどがあけます for
// ひきどをあけます), so it must not reach the 75 pass threshold.
const PARTICLES = new Set('はがをにでのもへと');
// Transcript similarity needed to pass a line. Phone speech recognition often returns partial
// sentences, so this is lenient; it is a text match, not a pronunciation score.
export const PASS_SCORE = 60;
// A particle-for-particle swap stays just below the pass mark, whatever the pass mark is.
const PARTICLE_SWAP_CAP = PASS_SCORE - 5;
function similarity(a, b, cap = true) {
  if (!a || !b) return 0;
  const d = [Array.from({ length: b.length + 1 }, (_, j) => j)];
  for (let i = 1; i <= a.length; i++) {
    d[i] = [i];
    for (let j = 1; j <= b.length; j++) d[i][j] = Math.min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
  }
  let swapped = false;
  for (let i = a.length, j = b.length; i > 0 && j > 0;) {
    if (d[i][j] === d[i - 1][j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1)) {
      if (a[i - 1] !== b[j - 1] && PARTICLES.has(a[i - 1]) && PARTICLES.has(b[j - 1])) swapped = true;
      i--; j--;
    } else if (d[i][j] === d[i - 1][j] + 1) i--;
    else j--;
  }
  // Same length and only particle-for-particle differences (the walk above can pick another path).
  if (!swapped && a.length === b.length) {
    const diffs = [...a].map((ch, k) => [ch, b[k]]).filter(([x, y]) => x !== y);
    swapped = diffs.length > 0 && diffs.every(([x, y]) => PARTICLES.has(x) && PARTICLES.has(y));
  }
  const score = Math.round(100 * (1 - d[a.length][b.length] / Math.max(a.length, b.length)));
  return swapped && cap ? Math.min(score, PARTICLE_SWAP_CAP) : score;
}
// A natural attention-getter before the target line is not an error (すみません、ひきどをあけます).
const POLITE_OPENER = /^(?:すみません|すいません|あのう|あの|ええと|えっと)/u;
export function scoreTranscript(transcript, line) {
  const actual = normalizeKana(transcript);
  const trimmed = actual.replace(POLITE_OPENER, '');
  // Generated scenarios list alternative proposed answers; any of them is a valid reply.
  const answers = [line, ...(Array.isArray(line.alternatives) ? line.alternatives : [])];
  const versus = (target) => [similarity(actual, target), trimmed !== actual && !POLITE_OPENER.test(target) ? similarity(trimmed, target) : 0];
  return Math.max(...answers.map((answer) => {
    const kana = normalizeKana(answer.kana);
    const best = Math.max(...versus(kana), ...versus(normalizeKana(answer.jp)));
    // A particle swap found against the reading caps this answer, even if the kanji text scores higher.
    const raw = Math.max(similarity(actual, kana, false), trimmed !== actual ? similarity(trimmed, kana, false) : 0);
    return raw > Math.max(...versus(kana)) ? Math.min(best, PARTICLE_SWAP_CAP) : best;
  }));
}
