import { scoreTranscript, PASS_SCORE } from './guardrails.js';
// Only known vocabulary supported by an encountered target and its authored reading is reported.
const COMMON_WORDS = [
  ['サンドイッチ', 'さんどいっち', 'sandwich'], ['おにぎり', 'おにぎり', 'rice ball'],
  ['商品', 'しょうひん', 'item'], ['会計', 'かいけい', 'checkout'], ['レジ', 'れじ', 'checkout counter'],
  ['かご', 'かご', 'basket'], ['袋', 'ふくろ', 'bag'], ['水', 'みず', 'water'],
].map(([jp, kana, en]) => ({ jp, kana, en }));
const fields = ['jp', 'kana', 'romaji', 'zh', 'en'];
export function targetId(flow) { return flow.active ? flow.current.nodeId : `main:${flow.mainBeat}`; }
function encounter(metrics, id, line) {
  if (!line?.jp) return null;
  let target = metrics.targets.find(item => item.id === id);
  if (!target && metrics.targets.length < 128) {
    target = { id, ...Object.fromEntries(fields.map(key => [key, line[key] || ''])), attempts: 0, bestTranscriptMatch: null, completed: false, manualSkips: 0, firstScore: null };
    metrics.targets.push(target);
  }
  return target;
}
export function recordSummary(state, { request, lesson, before, intent, response }) {
  const metrics = state.summary ||= { targets: [], words: [], scoredAttempts: 0, scoreSum: 0, hints: 0, manualSkips: 0 };
  // Every response overlay is actually encountered; authored but unseen lines are excluded.
  const target = before.started ? encounter(metrics, before.id, before.line) : null;
  encounter(metrics, targetId(state.flow), response.overlay);
  const c = request.context;
  const manual = c.gesture === 'pinch' || /^next$/i.test(c.glassesKey || '');
  const hint = c.gesture === 'long_pinch' || /hint|repeat/i.test(c.glassesKey || '');
  const advanced = before.nodeId !== state.flow.current.nodeId || Boolean(state.complete);
  if (!request.action && !intent && before.started) {
    if (hint) metrics.hints++;
    if (manual && advanced) { metrics.manualSkips++; if (target) target.manualSkips++; }
    const transcript = request.transcript?.trim();
    if (transcript && !manual && !hint && c.gesture !== 'point') {
      const score = scoreTranscript(transcript, before.line);
      metrics.scoredAttempts++; metrics.scoreSum += score;
      if (target) {
        if (!target.attempts) target.firstScore = score;
        target.attempts++;
        target.bestTranscriptMatch = Math.max(target.bestTranscriptMatch ?? 0, score);
        if (score >= PASS_SCORE && advanced) target.completed = true;
      }
    }
  }
  const shown = response.overlay;
  for (const word of [...lesson.beats.flatMap(beat => beat.vocab || []), ...COMMON_WORDS]) {
    // Kanji substring matches alone are not lexical evidence (水曜日 is not water).
    // Require the authored reading too; katakana-only words are self-identifying.
    const katakana = /^[\u30a0-\u30ffー]+$/u.test(word.jp || '');
    const readingMatches = word.kana && shown?.kana?.includes(word.kana);
    if (word.jp && shown?.jp?.includes(word.jp) && (katakana || readingMatches)
      && !metrics.words.some(item => item.jp === word.jp) && metrics.words.length < 128) {
      metrics.words.push({ jp: word.jp, kana: word.kana, en: word.en });
    }
  }
}
export function summarize(state, lessonId) {
  const metrics = state.summary || { targets: [], words: [], scoredAttempts: 0, scoreSum: 0, hints: 0, manualSkips: 0 };
  const attempted = metrics.targets.filter(target => target.attempts > 0);
  const firstSuccess = attempted.filter(target => target.firstScore >= PASS_SCORE).length;
  const targets = metrics.targets.map(({ firstScore, ...target }) => target);
  return structuredClone({ runId: state.flow.runId, revision: state.revision, lessonId, completed: Boolean(state.complete),
    targetsEncountered: targets.length, targetsAttempted: attempted.length, targetsCompleted: targets.filter(target => target.completed).length,
    scoredAttempts: metrics.scoredAttempts, averageTranscriptMatch: metrics.scoredAttempts ? Math.round(metrics.scoreSum / metrics.scoredAttempts) : null,
    firstAttempt: { successes: firstSuccess, total: attempted.length, rate: attempted.length ? Math.round(firstSuccess / attempted.length * 100) : null },
    hints: metrics.hints, manualSkips: metrics.manualSkips, words: metrics.words, targets,
    review: targets.filter(target => !target.completed || target.bestTranscriptMatch < PASS_SCORE).slice(0, 10),
  });
}
