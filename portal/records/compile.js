import { flattenLesson, validateLesson, AgentError } from '../agent/lessons.js';
export { PASS_SCORE } from '../agent/guardrails.js';
import { PASS_SCORE } from '../agent/guardrails.js';
const LINE_FIELDS = ['jp', 'kana', 'romaji', 'zh', 'en', 'cue'];
// Generated scenarios add the partner's prompt and alternative proposed answers.
const pickLine = (line) => line ? {
  ...Object.fromEntries(LINE_FIELDS.map((k) => [k, line[k]])),
  ...(line.partner !== undefined ? { partner: line.partner } : {}),
  ...(Array.isArray(line.alternatives) ? { alternatives: line.alternatives } : {}),
} : null;
const segmentOf = (route) => route?.kind === 'detour' ? { kind: 'detour', label: route.label } : { kind: 'main', label: null };
const keyOf = (segment, line, targetId) => targetId ? `target:${targetId}` : `legacy:${segment.kind}:${segment.label || ''}:${line.jp}`;
const PHOTO_PATH = /^photos\/ph_[a-f0-9]{16}$/;
const SAMPLE_PATH = /^lessons\/images\/[a-zA-Z0-9][a-zA-Z0-9_-]*\.(?:jpg|jpeg|png|webp)$/i;
const lastingImage = (image) => PHOTO_PATH.test(image) || SAMPLE_PATH.test(image) ? image : '';
const VOCAB_BUDGET = 32 * 1024;

/** Keep replay settings and beat ranges. Actual practiced lines already live in the turn log. */
export function snapshotReplaySource(lesson) {
  let vocabularyBytes = VOCAB_BUDGET;
  return {
    version: 1,
    title: lesson.title,
    level: lesson.level,
    scenePrompt: lesson.scenePrompt,
    audioPrompt: lesson.audioPrompt,
    startImage: lastingImage(lesson.startImage),
    ...(lesson.partnerVoice ? { partnerVoice: lesson.partnerVoice } : {}),
    ...(lesson.partnerRole ? { partnerRole: { ja: lesson.partnerRole.ja, en: lesson.partnerRole.en } } : {}),
    ...(lesson.visiblePartner !== undefined ? { visiblePartner: lesson.visiblePartner } : {}),
    // Lesson validation bounds groups, goal lengths and line counts. Vocabulary has a separate budget.
    beats: lesson.beats.map(({ goal, lines, vocab }) => ({
      goal, lineCount: lines.length,
      vocab: vocab.flatMap(({ jp, kana, en, objectHint }) => {
        if (vocabularyBytes <= 0) return [];
        const word = { jp, kana, en, objectHint };
        const bytes = Buffer.byteLength(JSON.stringify(word)) + 1;
        if (bytes > vocabularyBytes) return [];
        vocabularyBytes -= bytes;
        return [word];
      }),
    })),
  };
}

export function replayStartImage(record, source) {
  return record.photoId ? `photos/${record.photoId}` : record.sampleImage ? `lessons/images/${record.sampleImage}` : lastingImage(source.startImage);
}

/** Recorded turn event (written by agent/service.js) → reviewable line list. */
export function summarizeEvents(events) {
  const lines = new Map();
  const clerk = [];
  let detours = 0;
  const touch = (segment, line, targetId) => {
    const key = keyOf(segment, line, targetId);
    if (!lines.has(key)) lines.set(key, { segment, ...(targetId ? { targetId } : {}), line: pickLine(line), attempts: [], passed: false, skipped: false });
    return lines.get(key);
  };
  for (const event of events) {
    if (event.type === 'detour_plan') { detours++; continue; }
    if (event.type !== 'turn') continue;
    if (event.practiced) {
      const entry = touch(event.practicedSegment, event.practiced, event.practicedTargetId);
      if (typeof event.score === 'number') {
        entry.attempts.push({ transcript: event.transcript, score: event.score, at: event.at });
        if (event.score >= PASS_SCORE) entry.passed = true;
      }
      if (event.gesture === 'pinch' && !entry.passed) entry.skipped = true;
    }
    if (event.presented) touch(segmentOf(event.route), event.presented, event.presentedTargetId);
    if (event.clerk) clerk.push({ text: event.clerk, at: event.at });
  }
  const list = [...lines.values()];
  const attempted = list.filter((l) => l.attempts.length);
  const scores = attempted.map((l) => Math.max(...l.attempts.map((a) => a.score)));
  return {
    lines: list, clerk,
    stats: { lines: list.length, attempted: attempted.length, passed: list.filter((l) => l.passed).length, detours,
      bestAverage: scores.length ? Math.round(scores.reduce((a, b) => a + b, 0) / scores.length) : null },
  };
}

/** Recorded session → an ordinary lesson object, so the existing turn engine replays it. */
export function compileReplay({ id, record, summary, original, startImage, deck = 'all' }) {
  if (!['all', 'missed'].includes(deck)) throw new AgentError(400, 'deck must be all or missed');
  const snapshot = record.replaySource?.version === 1 ? record.replaySource : null;
  const source = snapshot || original;
  const mainTargets = snapshot
    ? snapshot.beats.flatMap((beat, beatIndex) => Array.from({ length: beat.lineCount }, () => ({ beat, beatIndex })))
    : flattenLesson(original);
  const legacyTargets = snapshot ? null : new Map(mainTargets.map((entry) => [entry.line.jp, entry]));
  const chosen = summary.lines.filter((l) => deck === 'all' || !l.passed);
  if (!chosen.length) throw new AgentError(409, deck === 'missed' ? 'Every line in this session was passed; nothing to review' : 'This session has no lines yet');
  const beats = [];
  for (const entry of chosen) {
    const mainIndex = /^main:(\d+)$/.exec(entry.targetId);
    const target = entry.segment.kind !== 'main' ? null
      : mainIndex ? mainTargets[Number(mainIndex[1])] : legacyTargets?.get(entry.line.jp);
    const groupKey = target ? `main:${target.beatIndex}` : `${entry.segment.kind}:${entry.segment.label}`;
    let beat = beats.at(-1);
    if (!beat || beat.key !== groupKey) {
      const goal = target?.beat.goal || (entry.segment.kind === 'main' ? 'Main lesson review' : `Side trip: ${entry.segment.label || 'extra practice'}`);
      beat = { key: groupKey, goal, lines: [], vocab: target?.beat.vocab || [] };
      beats.push(beat);
    }
    beat.lines.push(pickLine(entry.line));
  }
  const date = (record.startedAt || '').slice(0, 10);
  return validateLesson({
    id, title: `Replay · ${source.title}${date ? ` · ${date}` : ''}${deck === 'missed' ? ' · missed lines' : ''}`,
    level: source.level, scenePrompt: source.scenePrompt, audioPrompt: source.audioPrompt,
    startImage: startImage ?? replayStartImage(record, source),
    ...(source.partnerVoice ? { partnerVoice: source.partnerVoice } : {}),
    ...(source.partnerRole ? { partnerRole: source.partnerRole } : {}),
    ...(source.visiblePartner !== undefined ? { visiblePartner: source.visiblePartner } : {}),
    beats: beats.map(({ key, ...beat }) => beat),
    replay: { sourceRecordId: record.id, sourceLessonId: record.lessonId, deck },
  });
}
