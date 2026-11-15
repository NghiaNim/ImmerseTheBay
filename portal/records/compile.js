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
const keyOf = (segment, line) => `${segment.kind}:${segment.label || ''}:${line.jp}`;

/** Recorded turn event (written by agent/service.js) → reviewable line list. */
export function summarizeEvents(events) {
  const lines = new Map();
  const clerk = [];
  let detours = 0;
  const touch = (segment, line) => {
    const key = keyOf(segment, line);
    if (!lines.has(key)) lines.set(key, { segment, line: pickLine(line), attempts: [], passed: false, skipped: false });
    return lines.get(key);
  };
  for (const event of events) {
    if (event.type === 'detour_plan') { detours++; continue; }
    if (event.type !== 'turn') continue;
    if (event.practiced) {
      const entry = touch(event.practicedSegment, event.practiced);
      if (typeof event.score === 'number') {
        entry.attempts.push({ transcript: event.transcript, score: event.score, at: event.at });
        if (event.score >= PASS_SCORE) entry.passed = true;
      }
      if (event.gesture === 'pinch' && !entry.passed) entry.skipped = true;
    }
    if (event.presented) touch(segmentOf(event.route), event.presented);
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
export function compileReplay({ id, record, summary, original, deck = 'all' }) {
  if (!['all', 'missed'].includes(deck)) throw new AgentError(400, 'deck must be all or missed');
  const index = new Map(flattenLesson(original).map((entry) => [entry.line.jp, entry]));
  const chosen = summary.lines.filter((l) => deck === 'all' || !l.passed);
  if (!chosen.length) throw new AgentError(409, deck === 'missed' ? 'Every line in this session was passed; nothing to review' : 'This session has no lines yet');
  const beats = [];
  for (const entry of chosen) {
    const source = entry.segment.kind === 'main' ? index.get(entry.line.jp) : null;
    const groupKey = source ? `main:${original.beats.indexOf(source.beat)}` : `detour:${entry.segment.label}`;
    let beat = beats.at(-1);
    if (!beat || beat.key !== groupKey) {
      beat = { key: groupKey, goal: source ? source.beat.goal : `Side trip: ${entry.segment.label || 'extra practice'}`, lines: [], vocab: source ? source.beat.vocab : [] };
      beats.push(beat);
    }
    beat.lines.push(pickLine(entry.line));
  }
  const startImage = record.photoId ? `photos/${record.photoId}` : record.sampleImage ? `lessons/images/${record.sampleImage}` : original.startImage;
  const date = (record.startedAt || '').slice(0, 10);
  return validateLesson({
    id, title: `Replay · ${original.title}${date ? ` · ${date}` : ''}${deck === 'missed' ? ' · missed lines' : ''}`,
    level: original.level, scenePrompt: original.scenePrompt, audioPrompt: original.audioPrompt, startImage,
    ...(original.partnerRole ? { partnerRole: original.partnerRole } : {}),
    beats: beats.map(({ key, ...beat }) => beat),
    replay: { sourceRecordId: record.id, sourceLessonId: record.lessonId, deck },
  });
}
