import { mkdir, appendFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { AgentError, createLessonStore, flattenLesson } from './lessons.js';
import { hasBannedTerm, safePrompt, scoreTranscript } from './guardrails.js';
import { validateSchema } from './schema.js';
import { createClaudeClient } from './llm.js';
const LOG_ROOT = fileURLToPath(new URL('./logs/', import.meta.url));
const plain = (v) => v && typeof v === 'object' && !Array.isArray(v);
const bounded = (v, n) => typeof v === 'string' && v.length <= n;
function invalid(message) { throw new AgentError(400, message); }
export function validateRequest(body) {
  if (!plain(body)) invalid('JSON object required');
  if (Object.keys(body).some((k) => !['sessionId', 'lessonId', 'beat', 'transcript', 'imageRef', 'context'].includes(k))) invalid('Unknown request field');
  if (!bounded(body.sessionId, 160) || !/^[a-zA-Z0-9_.:-]+$/.test(body.sessionId)) invalid('Invalid sessionId');
  if (!bounded(body.lessonId, 80) || !/^[a-z0-9][a-z0-9-]*$/.test(body.lessonId)) invalid('Invalid lessonId');
  if (!Number.isInteger(body.beat) || body.beat < 0 || body.beat > 5000) invalid('beat must be a nonnegative flattened line index');
  if (body.transcript !== undefined && !bounded(body.transcript, 1000)) invalid('transcript must be at most 1000 characters');
  if (body.imageRef !== undefined && body.imageRef !== null) {
    const ref = body.imageRef;
    if (!(bounded(ref, 1000) || (plain(ref) && Object.keys(ref).every((k) => ['id', 'caption'].includes(k)) && bounded(ref.id, 1000) && (ref.caption === undefined || bounded(ref.caption, 1000))))) invalid('imageRef must be an opaque string or {id,caption}');
  }
  const c = body.context;
  if (!plain(c) || Object.keys(c).some((k) => !['gesture', 'pointedAt', 'roomLabels', 'planes', 'gaze', 'rms', 'glassesKey'].includes(k))) invalid('Invalid context');
  if (c.gesture !== undefined && ![null, 'pinch', 'long_pinch', 'point'].includes(c.gesture)) invalid('Invalid gesture');
  if (c.pointedAt !== undefined && c.pointedAt !== null && (!bounded(c.pointedAt, 40) || !/^[a-z][a-z0-9_ -]*$/.test(c.pointedAt))) invalid('Invalid pointedAt');
  if (c.roomLabels !== undefined && (!Array.isArray(c.roomLabels) || c.roomLabels.length > 20 || c.roomLabels.some((s) => !bounded(s, 40) || !/^[a-z][a-z0-9_ -]*$/.test(s)))) invalid('Invalid roomLabels');
  if (c.planes !== undefined && (!plain(c.planes) || Object.keys(c.planes).some((k) => k !== 'horizontalAtDeskHeight') || typeof c.planes.horizontalAtDeskHeight !== 'boolean')) invalid('Invalid planes');
  if (c.gaze !== undefined && !['visko', 'overlay', 'room'].includes(c.gaze)) invalid('Invalid gaze');
  if (c.rms !== undefined && (typeof c.rms !== 'number' || !Number.isFinite(c.rms) || c.rms < 0 || c.rms > 1)) invalid('rms must be between 0 and 1');
  if (c.glassesKey !== undefined && c.glassesKey !== null && !bounded(c.glassesKey, 80)) invalid('Invalid glassesKey');
  return structuredClone(body);
}
export async function writeDecision(entry) {
  await mkdir(LOG_ROOT, { recursive: true });
  await appendFile(path.join(LOG_ROOT, new Date().toISOString().slice(0, 10) + '.jsonl'), JSON.stringify(entry) + '\n', { mode: 0o600 });
}
function makeOverlay(line, hintLevel) { return line ? Object.fromEntries([...['jp', 'kana', 'romaji', 'zh', 'en'].map((k) => [k, line[k]]), ['hintLevel', hintLevel]]) : null; }
const NEUTRAL_PROMPT = 'The host waits beside the low table.';
function scriptTurn(request, lesson, state) {
  const all = flattenLesson(lesson);
  if (request.beat >= all.length) throw new AgentError(400, 'beat is outside this lesson');
  const current = all[request.beat];
  const c = request.context;
  const transcript = request.transcript?.trim() || '';
  const first = !state.started;
  const hint = c.gesture === 'long_pinch' || /repeat|hint/i.test(c.glassesKey || '');
  const point = c.gesture === 'point' && c.pointedAt;
  const score = transcript ? scoreTranscript(transcript, current.line) : null;
  const advance = !first && !hint && !point && (c.gesture === 'pinch' || /^next$/i.test(c.glassesKey || '') || score >= 75 && score !== null);
  const cursor = request.beat + Number(advance);
  const done = cursor >= all.length;
  const target = all[cursor] || current;
  if (hint || c.gaze === 'room' || (transcript && score < 75)) state.hintLevel = Math.min(3, state.hintLevel + 1);
  else if (advance) state.hintLevel = 0;
  const notes = [];
  if (transcript) notes.push('Text similarity only; this does not measure pronunciation.', score >= 75 ? 'The wording matches well. Continue with the next line.' : 'Try the target wording again; use the kana as a reading guide.');
  if (hint) notes.push(`Cue: ${current.line.cue}`);
  const vocab = point ? lesson.beats.flatMap((b) => b.vocab).find((v) => v.objectHint === c.pointedAt) : null;
  if (point) notes.push(vocab ? `${vocab.jp}（${vocab.kana}）: ${vocab.en}` : `No lesson vocabulary is assigned to ${c.pointedAt}.`);
  const labels = c.roomLabels || [];
  const nearby = target.beat.vocab.find((v) => labels.includes(v.objectHint));
  if (nearby) notes.push(`Nearby ${nearby.objectHint}: ${nearby.jp}（${nearby.kana}） — ${nearby.en}`);
  if (c.planes?.horizontalAtDeskHeight) notes.push('A desk-height surface is nearby; rehearse as if seated there.');
  if (c.gaze === 'room') notes.push('Look back at the sentence when you are ready.');
  if (c.rms !== undefined && c.rms < 0.01 && !transcript) notes.push('Speak when ready, or hold a pinch for a hint.');
  let prompt = first ? lesson.scenePrompt : target.line.cue;
  if (!first && point && vocab) prompt = `The camera moves closer to the ${vocab.objectHint}.`;
  else if (!first && nearby) prompt = `${target.line.cue} The ${nearby.objectHint} stays in view.`;
  if (first && plain(request.imageRef) && request.imageRef.caption && !hasBannedTerm(request.imageRef.caption)) notes.push('The selected photo anchors the opening scene.');
  prompt = safePrompt(prompt, NEUTRAL_PROMPT);
  const visko = done ? [{ name: 'pause', data: {} }] : [{ name: 'set_prompt', data: { prompt } }];
  if (first) visko.push({ name: 'set_audio_prompt', data: { prompt: safePrompt(lesson.audioPrompt, 'Quiet room ambience.') } }, { name: 'set_resolution', data: { resolution: 'native' } }, { name: 'start', data: {} });
  return { current, result: { visko, overlay: done ? null : makeOverlay(target.line, state.hintLevel), feedback: transcript || notes.length ? { score: score ?? 0, notes } : null, advance: done ? 'scene_done' : advance ? 'next_line' : 'stay' } };
}
function repairOutput(candidate, fallback) {
  if (!validateSchema(candidate)) throw new Error('LLM JSON schema mismatch');
  const result = structuredClone(fallback);
  // The LLM cannot skip lessons, falsify a score, send arbitrary commands or replace authored text.
  for (const command of result.visko) {
    if (!['set_prompt', 'set_audio_prompt'].includes(command.name)) continue;
    const proposed = candidate.visko.find((v) => v.name === command.name);
    command.data.prompt = safePrompt(proposed?.data?.prompt, command.data.prompt);
  }
  const notes = candidate.feedback?.notes.filter((s) => s.trim() && !hasBannedTerm(s)).slice(0, 3).map((s) => s.slice(0, 300)) || [];
  if (notes.length) result.feedback = { score: result.feedback?.score ?? 0, notes: [...(result.feedback?.notes || []), ...notes].slice(0, 10) };
  return result;
}
export function createAgentService({ store = createLessonStore(), llm = createClaudeClient(), log = writeDecision, now = Date.now, sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms)), promptIntervalMs = 2000, maxSessions = 500 } = {}) {
  const sessions = new Map();
  async function turn(input) {
    const request = validateRequest(input);
    const time = now();
    for (const [id, state] of sessions) if (!state.pending && time - state.touched > 6 * 60 * 60 * 1000) sessions.delete(id);
    let session = sessions.get(request.sessionId);
    if (!session) {
      if (sessions.size >= maxSessions) throw new AgentError(429, 'Too many active lesson sessions');
      session = { lessons: new Map(), tail: Promise.resolve(), lastPrompt: -Infinity, touched: time, pending: 0 };
      sessions.set(request.sessionId, session);
    }
    if (session.pending >= 4) throw new AgentError(429, 'Await the current turn before requesting another');
    session.pending++;
    const run = session.tail.then(async () => {
      const lesson = await store.load(request.lessonId);
      const state = { ...(session.lessons.get(request.lessonId) || { started: false, hintLevel: 0, attempts: 0 }) };
      state.attempts++;
      const { current, result: fallback } = scriptTurn(request, lesson, state);
      let response = fallback;
      let source = 'scripted';
      let failure = null;
      if (llm) {
        try {
          response = repairOutput(await llm({ lesson, current, transcript: request.transcript || '', context: request.context, photoCaption: plain(request.imageRef) ? request.imageRef.caption || '' : '', attempts: state.attempts, fallback: structuredClone(fallback) }), fallback);
          source = 'llm';
        } catch (e) { failure = e.message === 'LLM JSON schema mismatch' ? e.message : 'LLM unavailable or invalid response'; }
      }
      if (!validateSchema(response)) throw new Error('Internal response validation failed');
      if (response.visko.some((v) => v.name === 'set_prompt')) {
        const remaining = promptIntervalMs - (now() - session.lastPrompt);
        if (remaining > 0) await sleep(remaining);
        session.lastPrompt = now();
      }
      await log({ at: new Date(now()).toISOString(), sessionId: request.sessionId, lessonId: request.lessonId, beat: request.beat, transcript: request.transcript || '', context: request.context, photoCaption: plain(request.imageRef) ? request.imageRef.caption || '' : '', source, failure, response });
      state.started = true;
      session.lessons.set(request.lessonId, state);
      session.touched = now();
      return response;
    });
    session.tail = run.catch(() => {});
    try { return await run; } finally { session.pending--; }
  }
  function reset(input) {
    if (!plain(input) || Object.keys(input).some((k) => !['sessionId', 'lessonId'].includes(k))) invalid('Invalid reset request');
    const request = validateRequest({ ...input, beat: 0, context: {} });
    const session = sessions.get(request.sessionId);
    if (session?.pending) throw new AgentError(409, 'Await the current turn before resetting');
    session?.lessons.delete(request.lessonId);
    return { ok: true };
  }
  return { turn, reset };
}
