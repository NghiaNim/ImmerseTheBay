// Shared text-only model input. This is a byte bound, not a tokenizer estimate.
export const DEFAULT_CONTEXT_MAX_BYTES = 12_000;
const record = (value) => value && typeof value === 'object' && !Array.isArray(value) ? value : {};
const text = (value) => typeof value === 'string' ? value : undefined;
const number = (value) => Number.isFinite(value) ? value : undefined;
const strings = (source, fields) => Object.fromEntries(fields.filter((key) => typeof source?.[key] === 'string').map((key) => [key, source[key]]));
const lineFields = ['jp', 'kana', 'romaji', 'zh', 'en', 'cue'];
function line(value) { return strings(record(value), lineFields); }
function route(value) {
  const v = record(value);
  return { ...strings(v, ['kind', 'nodeId', 'label']), returnBeat: number(v.returnBeat), step: number(v.step), total: number(v.total) };
}
function fallback(value) {
  const v = record(value);
  const overlay = record(v.overlay);
  return {
    visko: Array.isArray(v.visko) ? v.visko.slice(0, 6).filter((item) => ['set_prompt', 'set_audio_prompt', 'set_resolution', 'start', 'pause', 'resume', 'reset'].includes(item?.name)).map((item) => ({ name: item.name, data: strings(record(item.data), item.name === 'set_resolution' ? ['resolution'] : ['set_prompt', 'set_audio_prompt'].includes(item.name) ? ['prompt'] : []) })) : [],
    overlay: v.overlay === null ? null : { ...line(overlay), hintLevel: number(overlay.hintLevel) },
    feedback: v.feedback == null ? null : { score: number(v.feedback.score), notes: Array.isArray(v.feedback.notes) ? v.feedback.notes.filter((note) => typeof note === 'string').slice(0, 10) : [] },
    advance: text(v.advance),
    ...(v.route ? { route: route(v.route) } : {}),
  };
}
function sceneContext(value) {
  if (!value) return undefined;
  const v = record(value);
  return {
    version: 1,
    ...strings(v, ['runId', 'referenceId', 'characterId', 'identityAnchor', 'sceneAnchor']),
    mainCheckpoint: { beat: number(v.mainCheckpoint?.beat), goal: text(v.mainCheckpoint?.goal) },
    // Issued commands are not evidence that the action appeared in the video.
    recentActions: Array.isArray(v.recentActions) ? v.recentActions.slice(-3).map((item) => ({ ...strings(record(item), ['nodeId', 'action']), status: 'issued_unverified' })) : [],
  };
}
function sensors(value) {
  const v = record(value);
  return {
    ...strings(v, ['gesture', 'pointedAt', 'gaze', 'glassesKey']),
    rms: number(v.rms),
    roomLabels: Array.isArray(v.roomLabels) ? v.roomLabels.filter((s) => typeof s === 'string').slice(0, 20) : undefined,
    planes: typeof v.planes?.horizontalAtDeskHeight === 'boolean' ? { horizontalAtDeskHeight: v.planes.horizontalAtDeskHeight } : undefined,
  };
}
export function buildModelContext(input, { planMode = false, maxBytes = DEFAULT_CONTEXT_MAX_BYTES } = {}) {
  if (!Number.isInteger(maxBytes) || maxBytes < 1) throw new Error('Invalid model context byte budget');
  const v = record(input);
  const current = record(v.current);
  const payload = {
    lesson: strings(record(v.lesson), ['title', 'level']),
    scenePrompt: text(v.lesson?.scenePrompt),
    current: { line: line(current.line), beat: { goal: text(current.beat?.goal) } },
    sceneContext: sceneContext(v.sceneContext),
    intent: text(v.intent), returnBeat: number(v.returnBeat),
    context: sensors(v.context),
    ...(!planMode ? { transcript: text(v.transcript), photoCaption: text(v.photoCaption), attempts: number(v.attempts), fallback: fallback(v.fallback) } : {}),
  };
  const size = () => Buffer.byteLength(JSON.stringify(payload), 'utf8');
  // Drop oldest optional memory first; never shorten identity, intent or return anchor.
  while (size() > maxBytes && payload.sceneContext?.recentActions.length) payload.sceneContext.recentActions.shift();
  if (size() > maxBytes) delete payload.photoCaption;
  if (size() > maxBytes) { delete payload.context.roomLabels; delete payload.context.planes; delete payload.context.rms; }
  if (size() > maxBytes && payload.fallback?.feedback) payload.fallback.feedback.notes = [];
  if (size() > maxBytes) throw new Error('Required model context exceeds byte budget');
  // Round-trip also removes undefined fields. Never truncate serialized JSON.
  return JSON.parse(JSON.stringify(payload));
}
