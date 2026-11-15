import { mkdir, appendFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { AgentError, createLessonStore, flattenLesson } from './lessons.js';
import { hasBannedTerm, safePrompt, scoreTranscript } from './guardrails.js';
import { validateSchema } from './schema.js';
import { validateOperation, createOperationCache, replayOperation, checkOperationState, rememberOperation, invalidateOperations } from './operations.js';
import { recordSummary, summarize, targetId } from './summary.js';
import { clerkSpeech } from './clerk-speech.js';
import { composeVisualPrompt, composeAmbientPrompt, CHARACTER_ANCHOR, SCENE_ANCHOR } from './continuity.js';
import { createLessonClient, createLessonPlanner } from './llm.js';
import { initializeFlow, detectedIntent, scriptedPlan, validatePlan, startDetour, detourTurn, moveMain, rejoinFlow } from './flow.js';
const LOG_ROOT = fileURLToPath(new URL('./logs/', import.meta.url));
const plain = (v) => v && typeof v === 'object' && !Array.isArray(v);
const bounded = (v, n) => typeof v === 'string' && v.length <= n;
function invalid(message) { throw new AgentError(400, message); }
export function validateRequest(body) {
  if (!plain(body)) invalid('JSON object required');
  if (Object.keys(body).some((k) => !['sessionId', 'lessonId', 'beat', 'transcript', 'intent', 'imageRef', 'context', 'operation', 'action'].includes(k))) invalid('Unknown request field');
  if (!bounded(body.sessionId, 160) || !/^[a-zA-Z0-9_.:-]+$/.test(body.sessionId)) invalid('Invalid sessionId');
  if (!bounded(body.lessonId, 80) || !/^[a-z0-9][a-z0-9-]*$/.test(body.lessonId)) invalid('Invalid lessonId');
  if (!Number.isInteger(body.beat) || body.beat < 0 || body.beat > 5000) invalid('beat must be a nonnegative flattened line index');
  if (body.intent !== undefined && (!bounded(body.intent, 500) || !body.intent.trim())) invalid('intent must be 1 to 500 characters');
  if (body.transcript !== undefined && !bounded(body.transcript, 1000)) invalid('transcript must be at most 1000 characters');
  if (body.imageRef !== undefined && body.imageRef !== null) {
    const ref = body.imageRef;
    if (!(bounded(ref, 1000) || (plain(ref) && Object.keys(ref).every((k) => ['id', 'caption'].includes(k)) && bounded(ref.id, 1000) && (ref.caption === undefined || bounded(ref.caption, 1000))))) invalid('imageRef must be an opaque string or {id,caption}');
  }
  if (body.action !== undefined && !['return_to_main', 'idle_hint'].includes(body.action)) invalid('Invalid agent action');
  if (body.operation !== undefined) validateOperation(body.operation);
  const c = body.context;
  if (!plain(c) || Object.keys(c).some((k) => !['gesture', 'pointedAt', 'roomLabels', 'planes', 'gaze', 'rms', 'glassesKey'].includes(k))) invalid('Invalid context');
  if (c.gesture !== undefined && ![null, 'pinch', 'long_pinch', 'point'].includes(c.gesture)) invalid('Invalid gesture');
  if (c.pointedAt !== undefined && c.pointedAt !== null && (!bounded(c.pointedAt, 40) || !/^[a-z][a-z0-9_ -]*$/.test(c.pointedAt))) invalid('Invalid pointedAt');
  if (c.roomLabels !== undefined && (!Array.isArray(c.roomLabels) || c.roomLabels.length > 20 || c.roomLabels.some((s) => !bounded(s, 40) || !/^[a-z][a-z0-9_ -]*$/.test(s)))) invalid('Invalid roomLabels');
  if (c.planes !== undefined && (!plain(c.planes) || Object.keys(c.planes).some((k) => k !== 'horizontalAtDeskHeight') || typeof c.planes.horizontalAtDeskHeight !== 'boolean')) invalid('Invalid planes');
  if (c.gaze !== undefined && !['visko', 'overlay', 'room'].includes(c.gaze)) invalid('Invalid gaze');
  if (c.rms !== undefined && (typeof c.rms !== 'number' || !Number.isFinite(c.rms) || c.rms < 0 || c.rms > 1)) invalid('rms must be between 0 and 1');
  if (c.glassesKey !== undefined && c.glassesKey !== null && !bounded(c.glassesKey, 80)) invalid('Invalid glassesKey');
  if (body.action && (body.intent || body.transcript || c.gesture || c.glassesKey)) invalid('Agent action cannot be combined with speech or navigation');
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
  if (!first && point && vocab) prompt = `The same ${/konbini|convenience/iu.test(lesson.id + lesson.title) ? 'clerk' : 'host'} stays in place and indicates the ${vocab.objectHint}.`;
  else if (!first && nearby) prompt = `${target.line.cue.replace(/[.!?。！？]+$/u, '')}, while the ${nearby.objectHint} stays in view.`;
  if (first && plain(request.imageRef) && request.imageRef.caption && !hasBannedTerm(request.imageRef.caption)) notes.push('The selected image anchors the opening scene.');
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
export function createAgentService({ store = createLessonStore(), llm = createLessonClient(), planner = createLessonPlanner(), log = writeDecision, now = Date.now, sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms)), promptIntervalMs = 2000, maxSessions = 500 } = {}) {
  const sessions = new Map();
  async function turn(input) {
    const request = validateRequest(input);
    const time = now();
    for (const [id, state] of sessions) if (!state.pending && time - state.touched > 6 * 60 * 60 * 1000) sessions.delete(id);
    let session = sessions.get(request.sessionId);
    if (!session) {
      if (sessions.size >= maxSessions) throw new AgentError(429, 'Too many active lesson sessions');
      session = { lessons: new Map(), tail: Promise.resolve(), lastPrompt: -Infinity, touched: time, pending: 0, operations: createOperationCache() };
      sessions.set(request.sessionId, session);
    }
    if (session.pending >= 4) throw new AgentError(429, 'Await the current turn before requesting another');
    session.pending++;
    const run = session.tail.then(async () => {
      const replay = replayOperation(session.operations, request);
      if (replay) {
        const current = session.lessons.get(request.lessonId);
        if (!current || current.flow.runId !== replay.operation.runId || current.revision !== replay.operation.revision) {
          throw new AgentError(409, 'Operation decision is stale; read the lesson snapshot');
        }
        session.touched = now(); return replay;
      }
      const existing = session.lessons.get(request.lessonId);
      checkOperationState(request, existing);
      const lesson = await store.load(request.lessonId);
      const state = structuredClone(existing || { started: false, hintLevel: 0, attempts: 0, revision: 0 });
      if (!request.action) state.attempts++;
      const lines = flattenLesson(lesson);
      if (request.beat >= lines.length) throw new AgentError(400, 'beat is outside this lesson');
      initializeFlow(state, request.beat);
      if (request.beat !== state.flow.mainBeat) throw new AgentError(409, 'beat must match the saved main checkpoint');
      if (state.complete) throw new AgentError(409, 'Lesson complete; reset before starting another run');
      if (request.action && !state.started) throw new AgentError(409, 'Start the lesson before requesting an agent action');
      const mainTarget = lines[state.flow.mainBeat];
      const suppliedReference = typeof request.imageRef === 'string' ? request.imageRef : request.imageRef?.id;
      if (state.sceneContext && suppliedReference && suppliedReference !== state.sceneContext.referenceId) {
        throw new AgentError(409, 'Starting image changed; reset the lesson before using a new reference');
      }
      if (!state.sceneContext) {
        state.sceneContext = {
          version: 1, runId: state.flow.runId,
          referenceId: suppliedReference || lesson.startImage || '',
          characterId: `${state.flow.runId}:cast`, identityAnchor: CHARACTER_ANCHOR,
          sceneAnchor: safePrompt(lesson.scenePrompt, NEUTRAL_PROMPT),
          mainCheckpoint: { beat: state.flow.mainBeat, goal: mainTarget.beat.goal }, recentActions: [],
        };
        state.referenceCaption = plain(request.imageRef) ? request.imageRef.caption || '' : '';
      }
      state.sceneContext.mainCheckpoint = { beat: state.flow.mainBeat, goal: mainTarget.beat.goal };
      state.sceneContext.phase = state.started ? 'continuation' : 'opening';
      state.sceneContext.cameraConstraint = SCENE_ANCHOR;

      const previousNodeId = state.flow.current.nodeId;
      const activeTarget = state.flow.active?.nodes[state.flow.active.index] || mainTarget.line;
      const summaryBefore = { started: state.started, id: targetId(state.flow), nodeId: previousNodeId, line: structuredClone(activeTarget) };
      const intent = request.action ? null : detectedIntent(request, activeTarget);
      let entered = false;
      let intentNote = null;
      let source = 'scripted';
      let failure = null;
      const sameIntent = intent && state.flow.active?.intent === intent;
      if (intent && !sameIntent) {
        if (state.flow.detoursUsed >= 6) intentNote = 'Six side interactions have been used in this run. Continue the main lesson or begin a new run.';
        else if (hasBannedTerm(intent)) intentNote = 'Please describe an ordinary original shopping goal without a character or brand.';
        else {
          let plan = scriptedPlan(intent, lesson, state.flow.mainBeat);
          // Known shopping branches work without a key; other explicit goals may be
          // planned by Claude. The LLM never controls the saved return checkpoint.
          if (!plan && planner) {
            try {
              plan = validatePlan(await planner({ lesson, current: mainTarget, intent, returnBeat: state.flow.mainBeat, context: request.context, sceneContext: structuredClone(state.sceneContext) }), state.flow.mainBeat);
              source = 'llm-detour';
            } catch { failure = 'Detour planner unavailable or unsafe response'; }
          }
          if (plan) {
            plan = validatePlan(plan, state.flow.mainBeat);
            if (state.flow.active) state.sceneContext.recentActions = [];
            startDetour(state, plan); state.flow.active.intent = intent; entered = true;
            if (source !== 'llm-detour') source = 'scripted-detour';
          } else intentNote = 'That side interaction is unavailable right now. Try a sandwich, a product shelf, or the checkout counter; your lesson is saved.';
        }
      }
      let current, fallback;
      const returned = request.action === 'return_to_main' && rejoinFlow(state);
      if (returned) state.sceneContext.recentActions = [];
      const branchTurn = Boolean(state.flow.active);
      if (request.action === 'idle_hint') {
        const invite = state.idleInvitedNode !== state.flow.current.nodeId;
        state.idleInvitedNode = state.flow.current.nodeId;
        current = { line: activeTarget, beat: mainTarget.beat };
        fallback = { visko: [], overlay: makeOverlay(activeTarget, state.hintLevel), feedback: null, advance: 'stay',
          speech: invite ? { role: 'clerk', language: 'ja', text: state.flow.active
            ? '別の商品をご覧になりますか。それとも、元のお話に戻りましょうか。'
            : '何かお探しですか。気になる商品があれば、お聞かせください。' } : null };
        source = invite ? 'idle-invitation' : 'idle-suppressed';
      } else if (request.action === 'return_to_main') {
        current = mainTarget;
        fallback = { visko: returned ? [{ name: 'set_prompt', data: { prompt: safePrompt(mainTarget.line.cue, NEUTRAL_PROMPT) } }] : [],
          overlay: makeOverlay(mainTarget.line, state.hintLevel), feedback: null, advance: 'stay' };
        source = returned ? 'explicit-rejoin' : 'already-main';
      } else if (branchTurn) {
        const branchRequest = intent ? { ...request, intent } : request;
        const branch = detourTurn(branchRequest, state, mainTarget, entered);
        if (intentNote) branch.notes.push(intentNote);
        current = { line: branch.current, beat: mainTarget.beat };
        const visko = [{ name: 'set_prompt', data: { prompt: safePrompt(state.started ? branch.cue : lesson.scenePrompt, 'The assistant waits beside the shelf.') } }];
        if (!state.started) visko.push({ name: 'set_audio_prompt', data: { prompt: safePrompt(lesson.audioPrompt, 'Quiet store ambience.') } }, { name: 'set_resolution', data: { resolution: 'native' } }, { name: 'start', data: {} });
        fallback = { visko, overlay: makeOverlay(branch.line, state.hintLevel), feedback: branch.notes.length ? { score: branch.score ?? 0, notes: branch.notes } : null, advance: 'stay' };
      } else {
        // Explicit/off-script goals are not scored as failed pronunciation or
        // allowed to accidentally advance the interrupted main line.
        const scripted = scriptTurn(intent ? { ...request, transcript: '', context: { ...request.context, gesture: null, glassesKey: null } } : request, lesson, state);
        current = scripted.current; fallback = scripted.result;
        if (fallback.advance === 'next_line') moveMain(state, request.beat + 1);
        if (fallback.advance === 'scene_done') state.complete = true;
        if (intentNote) fallback.feedback = { score: 0, notes: [...(fallback.feedback?.notes || []), intentNote] };
      }
      fallback.route = { ...state.flow.current };
      const savedTarget = lines[state.flow.mainBeat];
      state.sceneContext.mainCheckpoint = { beat: state.flow.mainBeat, goal: savedTarget?.beat.goal || 'Lesson complete' };
      // Completed branch actions must not act as goals on the resumed main line.
      if (branchTurn && fallback.route.kind === 'rejoin') state.sceneContext.recentActions = [];
      let response = fallback;
      // Gesture-only/context-only navigation uses authored deltas immediately;
      // reserve optional model refinement for opening and actual spoken practice.
      if (llm && !request.action && !branchTurn && !intent && (!state.started || request.transcript?.trim())) {
        try {
          response = repairOutput(await llm({ lesson, current, transcript: request.transcript || '', context: request.context, photoCaption: state.referenceCaption, sceneContext: structuredClone(state.sceneContext), attempts: state.attempts, fallback: structuredClone(fallback) }), fallback);
          source = 'llm';
        } catch (e) { failure = e.message === 'LLM JSON schema mismatch' ? e.message : 'LLM unavailable or invalid response'; }
      }
      if (request.action !== 'idle_hint') response.speech = !state.started || previousNodeId !== response.route.nodeId
        ? clerkSpeech({ first: !state.started, route: response.route, cue: fallback.visko.find((item) => item.name === 'set_prompt')?.data.prompt, store: /konbini|convenience/iu.test(lesson.id + lesson.title) }) : null;
      for (const command of response.visko) {
        if (command.name === 'set_audio_prompt') command.data.prompt = composeAmbientPrompt(command.data.prompt);
        if (command.name !== 'set_prompt') continue;
        const authored = fallback.visko.find((item) => item.name === 'set_prompt')?.data.prompt;
        command.data.prompt = composeVisualPrompt(state.started ? command.data.prompt : state.sceneContext.sceneAnchor, authored, { initial: !state.started, speaking: Boolean(response.speech) && lesson.visiblePartner !== false, visiblePartner: lesson.visiblePartner !== false });
      }
      if (!validateSchema(response)) throw new Error('Internal response validation failed');
      if (response.visko.some((v) => v.name === 'set_prompt')) {
        const remaining = promptIntervalMs - (now() - session.lastPrompt);
        if (remaining > 0) await sleep(remaining);
        session.lastPrompt = now();
      }
      state.revision = (state.revision || 0) + 1;
      if (request.operation) response.operation = { requestId: request.operation.requestId, runId: state.flow.runId, revision: state.revision, replayed: false };
      recordSummary(state, { request, lesson, before: summaryBefore, intent, response });
      await log({ at: new Date(now()).toISOString(), sessionId: request.sessionId, lessonId: request.lessonId, beat: request.beat, transcript: request.transcript || '', intent: intent || null, context: request.context, photoCaption: plain(request.imageRef) ? request.imageRef.caption || '' : '', source, failure, route: response.route, graph: { nodes: state.flow.nodes, edges: state.flow.edges }, response });
      const visual = response.visko.find((command) => command.name === 'set_prompt');
      if (visual) {
        // Issued decisions are not proof of provider acceptance or visual adoption.
        const recent = state.sceneContext.recentActions.filter((item) => item.nodeId !== response.route.nodeId);
        state.sceneContext.recentActions = [...recent, { nodeId: response.route.nodeId, action: visual.data.prompt, status: 'issued_unverified' }].slice(-3);
      }
      state.started = true;
      state.lastResponse = structuredClone(response);
      session.lessons.set(request.lessonId, state);
      rememberOperation(session.operations, request, response);
      session.touched = now();
      return response;
    });
    session.tail = run.catch(() => {});
    try { return await run; } finally {
      session.pending--;
      if (!session.pending && !session.lessons.size && !session.operations.retired.size) sessions.delete(request.sessionId);
    }
  }
  function reset(input) {
    if (!plain(input) || Object.keys(input).some((k) => !['sessionId', 'lessonId'].includes(k))) invalid('Invalid reset request');
    const request = validateRequest({ ...input, beat: 0, context: {} });
    const session = sessions.get(request.sessionId);
    if (session?.pending) throw new AgentError(409, 'Await the current turn before resetting');
    if (session) invalidateOperations(session.operations, request.lessonId);
    session?.lessons.delete(request.lessonId);
    return { ok: true };
  }
  function snapshot(input) {
    if (!plain(input) || Object.keys(input).some((key) => !['sessionId', 'lessonId'].includes(key))) invalid('Invalid snapshot request');
    const request = validateRequest({ ...input, beat: 0, context: {} });
    const session = sessions.get(request.sessionId);
    if (session?.pending) throw new AgentError(409, 'Await the current turn before reading a snapshot');
    const state = session?.lessons.get(request.lessonId);
    if (!state) return null;
    session.touched = now();
    return structuredClone({ runId: state.flow.runId, revision: state.revision, beat: state.flow.mainBeat, response: state.lastResponse, lessonContext: { scene: state.sceneContext.sceneAnchor.slice(0, 1000), goal: state.sceneContext.mainCheckpoint.goal.slice(0, 1000) } });
  }
  function summary(input) {
    // Reuse the exact identity validation and pending-operation barrier.
    const current = snapshot(input);
    if (!current) return null;
    return summarize(sessions.get(input.sessionId).lessons.get(input.lessonId), input.lessonId);
  }
  return { turn, reset, snapshot, summary };
}
