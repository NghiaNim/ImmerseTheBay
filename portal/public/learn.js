import { connectWithCapacityRetry } from './dynamic-retry.js';
import { installScenePoint } from './scene-point.js';
import { createLessonSpeech } from './lesson-speech.js';
import { createLessonOperations } from './lesson-operations.js';
import { createLineVoice, assertVoiceRevision } from './line-voice.js';
import { createAgentMachine, createIdleInvitation } from './agent-machine.js';
// SDK 3.4.0 signatures verified against its published declarations and Reactor's
// reactor-class / Visko stable schema docs. sendCommand reports errors via replies/events.
const $ = (id) => document.getElementById(id);
const MODELS = { stable: 'reactor/visko-orbis-stable', dynamic: 'reactor/visko-orbis-dynamic' };
const SAMPLES = [
  { file: 'konbini-checkout-original.png', label: 'Convenience-store checkout · original illustration', caption: 'Original illustrated convenience-store checkout with an adult clerk, a counter and shelves of plainly packaged food and drinks.' },
  { file: 'tokyo-house-interior.jpg', label: 'Tokyo home · tatami room', caption: 'Traditional Japanese house interior with wooden ceiling, sliding doors and floor.' },
  { file: 'historic-japanese-interior.jpg', label: 'Historic Japanese room', caption: 'Historic Japanese room with tatami floor and sliding screens.' },
  { file: 'tokyo-garden.jpg', label: 'Tokyo garden', caption: 'Japanese garden with a pine tree, stone lantern and pond.' },
];
const token = new URLSearchParams(location.search).get('portal_token') || '';
const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
let sdkPromise, reactor, sessionId, model, sharedSession, hub, hubRetry, recognition;
let lesson, catalog = [], beat = 0, busy = false, active = false, finished = false, preview = false;
let closing = false, epoch = 0, apiAbort, photoFile, photoCaption = '', previewUrl, imageRef;
let lastSdkError = null, lastPromptAt = 0, latestDecision = null;
let availableResolutions = [], webResolution = 'native';
let nextAllowedConnectAt = 0, lastRateLimitReason = 'rate_limit';
let runtimeEvents = [], runtimeState = emptyRuntimeState();
let mediaStream = new MediaStream();
let recording = null, micStarting = false, transcribing = false, speechAttempt = 0;
let clerkSpeech = null, ttsBusy = false, voiceActive = false, voiceReconciling = false, voiceUnsafe = false, voiceCheckRunning = false;
let lastMediaTime = 0, lastMediaProgressAt = -Infinity;
const agentMachine = createAgentMachine({ onChange: (state) => { document.body.dataset.agentState = state; $('runtimeTrace').dataset.agentState = state; idleInvitation.sync(latestDecision?.route?.nodeId || null); } });
const idleInvitation = createIdleInvitation({
  eligible: () => active && !voiceActive && !voiceReconciling && !voiceUnsafe && !busy && !finished && agentMachine.state === 'waiting'
    && document.visibilityState === 'visible' && !recognition && !recording && !micStarting && !transcribing && !ttsBusy
    && (preview || (!$('video').paused && $('video').videoWidth > 0 && performance.now() - lastMediaProgressAt < 2000)),
  invite: () => { void turn({ action: 'idle_hint' }); },
});
const lessonOperations = createLessonOperations({ headers: authHeaders });
const lessonSpeech = createLessonSpeech({
  video: $('video'), headers: authHeaders,
  isMicrophoneActive: () => !!recognition || !!recording || micStarting || transcribing,
  onState: ({ state, role, message }) => {
    ttsBusy = state === 'loading' || state === 'playing';
    idleInvitation.sync(latestDecision?.route?.nodeId || null);
    const label = role === 'clerk' ? '店员' : 'Your line';
    $('ttsStatus').textContent = state === 'loading' ? `${label} · preparing audio…`
      : state === 'playing' ? `${label} · speaking…`
      : state === 'error' ? `${label} · ${message} ${role === 'clerk' ? 'Press 店员重播 to retry.' : 'Press Listen to retry.'}` : '';
    $('ttsStatus').classList.toggle('error', state === 'error');
  },
});

const lineVoice = createLineVoice({
  getSession: async () => {
    const run = epoch;
    const session = await api('/api/voice/token', { sessionId, lessonId: lesson.id }, apiAbort.signal);
    assertCurrent(run);
    try { assertVoiceRevision(session, latestDecision?.operation); }
    catch (error) { voiceUnsafe = true; throw error; }
    return session;
  },
  onState: ({ state, message }) => {
    const wasActive = voiceActive;
    voiceActive = state !== 'off'; document.body.dataset.voiceState = state;
    if (state === 'off' && wasActive && active) { voiceReconciling = true; void reconcileVoice(); }
    $('conversationStatus').dataset.state = state;
    $('conversationStatus').textContent = message || ({ off: '', connecting: 'Connecting voice…', listening: 'Listening · 日本語 / English', speaking: '店员 speaking · you can interrupt' })[state];
    refresh();
  },
  onText: (text, role) => { if (role === 'user') $('transcript').textContent = text; else { $('clerkCaption').textContent = `店员 · ${text}`; $('clerkCaption').hidden = false; } },
  onTool: async (call, current) => {
    if (!current() || !active || finished) throw new Error('The lesson is not active.');
    const parameters = call.parameters || {};
    let input;
    if (call.tool_name === 'branch_scene' && typeof parameters.intent === 'string' && parameters.intent.trim()) input = { intent: parameters.intent.slice(0, 1000) };
    else if (call.tool_name === 'return_to_main') input = { action: 'return_to_main' };
    else if (call.tool_name === 'rehearse_line' && typeof parameters.transcript === 'string' && parameters.transcript.trim()) input = { transcript: parameters.transcript.slice(0, 2000) };
    else throw new Error('Unsupported lesson tool or invalid arguments.');
    const accepted = await turn({ ...input, fromVoice: true });
    if (!accepted) throw new Error('Lesson change was not confirmed. Do not claim it succeeded.');
    return { accepted: true, route: latestDecision?.route, feedback: latestDecision?.feedback, overlay: latestDecision?.overlay };
  },
});

async function reconcileVoice() {
  if (!voiceReconciling || voiceCheckRunning || busy || !active) return;
  const run = epoch; voiceCheckRunning = true;
  $('conversationStatus').textContent = 'Checking lesson state…';
  try {
    const snapshot = await api('/api/agent/snapshot', { sessionId, lessonId: lesson.id }, apiAbort.signal);
    assertCurrent(run); assertVoiceRevision(snapshot, latestDecision?.operation);
  } catch (error) {
    if (run === epoch) { voiceUnsafe = true; notify('Lesson state could not be confirmed. Leave and restart safely; no scene commands were replayed.', true); }
  } finally {
    if (run === epoch) {
      voiceCheckRunning = false; voiceReconciling = false;
      $('conversationStatus').textContent = voiceUnsafe ? 'Leave and restart to recover lesson state.' : 'Manual controls ready.';
      refresh();
    }
  }
}

if (token) $('homeLink').href = `/?portal_token=${encodeURIComponent(token)}`;

function emptyRuntimeState() {
  return { lastChunkIndex: null, acceptedPrompt: null, activePrompt: null, currentPrompt: null, hasAppliedPromptTelemetry: false, framesEmitted: null, audioSamples: null, delivered: null };
}
function captureAcceptedPrompt(message) {
  if (message?.type === 'prompt_accepted' && typeof message.data?.prompt === 'string') runtimeState.acceptedPrompt = message.data.prompt.slice(0, 1000);
}
function recordRuntime(kind, data = {}) {
  runtimeState.hasAppliedPromptTelemetry = runtimeState.activePrompt !== null || runtimeState.currentPrompt !== null;
  runtimeEvents.push({ at: new Date().toISOString(), kind, ...data });
  runtimeEvents = runtimeEvents.slice(-16);
  $('runtimeTrace').textContent = JSON.stringify({ agentState: agentMachine.state, model, sessionId, resolution: webResolution, ...runtimeState, events: runtimeEvents }, null, 2);
}
function notify(text, error = false) { $('notice').textContent = text; $('notice').classList.toggle('error', error); }
function refresh() {
  $('conversation').disabled = !voiceActive && (!active || finished || busy || voiceReconciling || voiceUnsafe);
  $('conversation').textContent = voiceActive ? 'End conversation' : 'Conversation';
  $('conversation').setAttribute('aria-pressed', String(voiceActive));
  $('begin').disabled = busy || active || !lesson;
  $('begin').textContent = $('previewOnly').checked ? 'Start practice' : 'Start live lesson';
  $('leave').disabled = !active && !busy && !reactor;
  for (const id of ['lesson', 'sample', 'photo', 'model', 'sessionId', 'previewOnly']) $(id).disabled = busy || active;
  $('useShared').disabled = busy || active || !sharedSession;
  const speechInUse = voiceActive || voiceReconciling || voiceUnsafe || !!recognition || !!recording || micStarting || transcribing;
  for (const id of ['pinch', 'longPinch', 'point', 'sendContext']) $(id).disabled = !active || busy || finished || speechInUse;
  const engineAvailable = $('speechEngine').value === 'browser' ? !!SpeechRecognition : !!window.MediaRecorder && !!navigator.mediaDevices?.getUserMedia;
  $('speak').disabled = !active || busy || finished || !engineAvailable || speechInUse;
  $('stopSpeech').disabled = !recognition && !recording && !micStarting;
  $('stopSpeech').hidden = !recognition && !recording && !micStarting;
  $('speechMode').disabled = busy || speechInUse; $('intentLanguage').disabled = speechInUse; $('speechEngine').disabled = speechInUse;
  $('speak').textContent = $('speechMode').value === 'intent' ? 'Tell guide' : 'Speak Japanese';
  $('listen').disabled = !active || busy || finished || speechInUse || !latestDecision?.overlay?.jp;
  $('clerkReplay').disabled = !active || busy || speechInUse || !clerkSpeech;
  $('returnMain').hidden = !active || latestDecision?.route?.kind !== 'detour';
  $('returnMain').disabled = !active || busy || finished || speechInUse;
  idleInvitation.sync(latestDecision?.route?.nodeId || null);
  $('sound').disabled = !reactor;
  $('sessionInfo').textContent = sessionId ? `${preview ? 'Practice' : model} · ${sessionId}` : sharedSession ? `Shared session available: ${sharedSession.sessionId}` : '';
}
function authHeaders() { return token ? { portal_token: token } : {}; }
async function api(path, body, signal) {
  const response = await fetch(path, { method: body === undefined ? 'GET' : 'POST', headers: { ...authHeaders(), ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) }, body: body === undefined ? undefined : JSON.stringify(body), signal });
  const data = await response.json();
  if (!response.ok) {
    const error = new Error(data.error || `Portal returned ${response.status}`);
    error.status = response.status; error.retryAfterHeader = response.headers.get('retry-after'); throw error;
  }
  return data;
}
function hubSend(data) { if (hub?.readyState === WebSocket.OPEN) hub.send(JSON.stringify(data)); }
function connectHub() {
  if (closing) return;
  hub = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/sync${token ? `?portal_token=${encodeURIComponent(token)}` : ''}`);
  hub.onopen = () => { $('syncStatus').textContent = 'Sync online'; hubSend({ type: 'hello', role: 'web', name: 'Japanese learning' }); };
  hub.onclose = () => { $('syncStatus').textContent = 'Sync offline'; if (!closing) hubRetry = setTimeout(connectHub, 2000); };
  hub.onmessage = (event) => {
    let msg; try { msg = JSON.parse(event.data); } catch { return; }
    if (msg.type === 'welcome' && msg.session) sharedSession = msg.session;
    if (msg.type === 'session') sharedSession = msg;
    if (msg.type === 'session_end') {
      sharedSession = null;
      if (active && !preview && (!msg.sessionId || msg.sessionId === sessionId)) void leave('The shared session ended.');
    }
    // Learning messages are display-only: never execute the same decisions twice.
    if (msg.type === 'learn' && msg.sessionId === sessionId && msg.from === 'unity') {
      if (Number.isInteger(msg.beat)) beat = msg.beat;
      finished = msg.advance === 'scene_done'; renderDecision(msg, Boolean(msg.transcript));
    }
    // The owning web client can execute commands relayed by Unity's ViskoSession.
    if (msg.type === 'command' && msg.from === 'unity' && reactor && (!msg.sessionId || msg.sessionId === sessionId)) {
      void command(msg.name, msg.data || {}).catch((e) => notify(e.message, true));
    }
    refresh();
  };
}
async function loadCatalog() {
  try {
    const data = await api('/api/agent/lessons'); catalog = data.lessons;
    if (!Array.isArray(catalog) || !catalog.length) throw new Error('No lessons are available yet.');
    $('lesson').replaceChildren(...catalog.map((item) => new Option(item.title, item.id)));
    if (catalog.some((item) => item.id === 'ja-konbini-001')) $('lesson').value = 'ja-konbini-001';
    await chooseLesson();
  } catch (error) { notify(`Could not load lessons: ${error.message}`, true); }
}
async function chooseLesson() {
  lessonSpeech.cancel(); clerkSpeech = null; $('clerkCaption').hidden = true; $('clerkReplay').hidden = true;
  busy = true; refresh();
  try {
    lesson = await api(`/api/agent/lessons/${encodeURIComponent($('lesson').value)}`);
    const count = lesson.beats.reduce((n, b) => n + b.lines.length, 0);
    $('lessonInfo').textContent = `${lesson.level} · ${lesson.beats.length} moments · ${count} spoken lines`;
    const shopping = lesson.id === 'ja-konbini-001';
    $('lessonLabel').textContent = shopping ? 'CONVENIENCE-STORE JAPANESE' : lesson.id === 'ja-home-001' ? 'JAPANESE AT HOME' : 'JAPANESE PRACTICE';
    document.title = `${shopping ? 'Convenience-store Japanese' : lesson.title} · ImmerseTheBay`;
    $('progress').textContent = $('lessonLabel').textContent;
    $('jp').textContent = shopping ? '買い物のひとこまから。' : '日常のひとこまから。';
    $('en').textContent = shopping ? 'Step into the shop. Practice a real conversation.' : 'Step into the scene. Practice a real conversation.';
    for (const field of ['kana', 'romaji', 'zh', 'feedback']) $(field).textContent = '';
    const filename = String(lesson.startImage || '').split('/').pop();
    if (filename && !SAMPLES.some((sample) => sample.file === filename)) {
      SAMPLES.push({ file: filename, label: shopping ? 'Convenience-store shopping' : `${lesson.title} · start photo`, caption: `Start photo for ${lesson.title}; visual contents have not been analyzed.` });
      $('sample').add(new Option(SAMPLES.at(-1).label, filename));
    }
    clearPhoto();
    if (filename) { $('sample').value = filename; await chooseSample(); } else $('sample').value = '';
    $('scenePlaceholder').querySelector('span').textContent = shopping ? 'いらっしゃいませ。' : '日本語で話そう。';
    $('scenePlaceholder').querySelector('p').textContent = shopping ? 'A small shop. An everyday conversation.' : 'Your next conversation starts here.';
  } catch (error) { lesson = null; notify(error.message, true); }
  finally { busy = false; refresh(); }
}
function clearPhoto() {
  photoFile = null; photoCaption = '';
  if (previewUrl) URL.revokeObjectURL(previewUrl); previewUrl = null;
  $('photoPreview').hidden = true; $('sceneFocus').hidden = true; $('scenePlaceholder').hidden = false; $('photo').value = '';
}
function setPhoto(file, caption) {
  photoFile = file; photoCaption = caption;
  if (previewUrl) URL.revokeObjectURL(previewUrl);
  previewUrl = URL.createObjectURL(file); $('photoPreview').src = previewUrl; $('sceneFocus').src = previewUrl; $('sceneFocus').hidden = false;
  $('photoPreview').hidden = false; $('scenePlaceholder').hidden = true;
  refresh();
}
async function chooseSample() {
  const sample = SAMPLES.find((item) => item.file === $('sample').value);
  if (!sample) { clearPhoto(); return; }
  const response = await fetch(`/lessons/images/${encodeURIComponent(sample.file)}`, { headers: authHeaders() });
  if (!response.ok) throw new Error('This sample is unavailable. Upload your own photo to continue.');
  const blob = await response.blob();
  setPhoto(new File([blob], sample.file, { type: blob.type || 'image/jpeg' }), sample.caption);
  $('photo').value = '';
}
// Center crop rather than allowing Reactor to squash a 4:3 phone photo.
async function preparePhoto(file) {
  const bitmap = await createImageBitmap(file);
  try {
    const canvas = document.createElement('canvas'); canvas.width = 1280; canvas.height = 720;
    const ratio = 16 / 9; const w = Math.min(bitmap.width, bitmap.height * ratio); const h = w / ratio;
    canvas.getContext('2d').drawImage(bitmap, (bitmap.width - w) / 2, (bitmap.height - h) / 2, w, h, 0, 0, 1280, 720);
    const blob = await new Promise((resolve, reject) => canvas.toBlob((b) => b ? resolve(b) : reject(new Error('Could not prepare photo.')), 'image/jpeg', 0.9));
    return new File([blob], 'lesson-start.jpg', { type: 'image/jpeg' });
  } finally { bitmap.close(); }
}
function assertCurrent(run) { if (run !== epoch || closing) throw new DOMException('Session cancelled', 'AbortError'); }
async function command(name, data = {}, client = reactor) {
  if (!client) return;
  lastSdkError = null;
  const reply = await client.sendCommand(name, data);
  if (client === reactor) {
    captureAcceptedPrompt(reply);
    recordRuntime('command_reply', { name, type: reply?.type || 'ack', ...(reply?.data?.reason ? { reason: String(reply.data.reason).slice(0, 1000) } : {}) });
  }
  if (reply?.type === 'command_error') throw new Error(reply.data?.reason || `${name} was rejected.`);
  if (lastSdkError) throw new Error(lastSdkError.message || String(lastSdkError));
  return reply;
}
async function connectReactor(targetModel, joinId, run) {
  const credentials = await api('/api/token', { model: targetModel, ...(joinId ? { sessionId: joinId } : {}) }, apiAbort.signal);
  assertCurrent(run);
  sdkPromise ||= import('https://esm.sh/@reactor-team/js-sdk@3.4.0');
  const { Reactor } = await sdkPromise; assertCurrent(run);
  const client = new Reactor({ modelName: targetModel }); reactor = client; model = targetModel; recordRuntime('connect', { model: targetModel });
  client.on('error', (error) => { if (client === reactor) { lastSdkError = error; recordRuntime('sdk_error', { message: String(error?.message || error).slice(0, 1000) }); } });
  client.on('statusChanged', (status) => {
    if (client !== reactor || run !== epoch) return;
    $('sceneStatus').textContent = `Visko ${status}`;
    if (status === 'disconnected' && active) void leave('Disconnected from Visko. Begin a new lesson to reconnect.');
  });
  client.on('trackReceived', (_name, track) => {
    if (client !== reactor || run !== epoch) { track.stop(); return; }
    for (const previous of mediaStream.getTracks().filter((t) => t.kind === track.kind)) { mediaStream.removeTrack(previous); previous.stop(); }
    mediaStream.addTrack(track); $('video').srcObject = mediaStream;
    if (track.kind === 'video') { $('photoPreview').hidden = !photoFile; $('sceneFocus').hidden = true; $('scenePlaceholder').hidden = true; }
    void $('video').play().catch(() => notify('Press Unmute scene to enable playback.'));
  });
  client.on('message', (msg) => {
    if (client !== reactor || run !== epoch) return;
    if (msg.type === 'prompt_accepted') { captureAcceptedPrompt(msg); recordRuntime('prompt_accepted', { acceptedPrompt: runtimeState.acceptedPrompt }); }
    if (msg.type === 'chunk_complete') {
      $('sceneStatus').textContent = `Live · ${webResolution} · chunk ${msg.data?.chunk_index ?? ''}`;
      runtimeState.lastChunkIndex = msg.data?.chunk_index ?? null;
      runtimeState.activePrompt = typeof msg.data?.active_prompt === 'string' ? msg.data.active_prompt.slice(0, 1000) : null;
      runtimeState.framesEmitted = Number.isFinite(msg.data?.frames_emitted) ? msg.data.frames_emitted : null;
      runtimeState.audioSamples = Number.isFinite(msg.data?.audio_samples) ? msg.data.audio_samples : null;
      runtimeState.delivered = typeof msg.data?.delivered === 'string' ? msg.data.delivered.slice(0, 64) : null;
      recordRuntime('chunk_complete', { chunkIndex: runtimeState.lastChunkIndex, activePrompt: runtimeState.activePrompt, framesEmitted: runtimeState.framesEmitted, audioSamples: runtimeState.audioSamples, delivered: runtimeState.delivered });
    }
    if (msg.type === 'command_error') { lastSdkError = new Error(msg.data?.reason || 'Scene command rejected'); notify(lastSdkError.message, true); recordRuntime('command_error', { reason: lastSdkError.message.slice(0, 1000) }); }
    if (msg.type === 'state') {
      if (Array.isArray(msg.data?.available_resolutions)) availableResolutions = msg.data.available_resolutions;
      runtimeState.currentPrompt = typeof msg.data?.current_prompt === 'string' ? msg.data.current_prompt.slice(0, 1000) : null;
      recordRuntime('state', { currentPrompt: runtimeState.currentPrompt, availableResolutions });
      hubSend({ type: 'state', sessionId, data: msg.data });
    }
  });
  try { await client.connect(credentials.jwt, joinId ? { sessionId: joinId } : undefined); assertCurrent(run); }
  catch (error) {
    const allocatedSession = client.getSessionId();
    let cleanupTimer, cleanupFailed = false;
    try { await Promise.race([client.disconnect(), new Promise((_, reject) => { cleanupTimer = setTimeout(() => reject(new Error('Disconnect timeout')), 5000); })]); }
    catch { cleanupFailed = true; }
    finally { clearTimeout(cleanupTimer); if (reactor === client && !cleanupFailed) reactor = null; }
    if (allocatedSession || cleanupFailed) {
      const blocked = new Error(`${error?.message || 'Connection failed'}. Automatic retry stopped because ${allocatedSession ? 'a session was allocated' : 'disconnect was not confirmed'}.`);
      Object.assign(blocked, { noRetry: true, status: error?.status, code: error?.code, retry_after_ms: error?.retry_after_ms, cause: error }); throw blocked;
    }
    throw error;
  }
  sessionId = client.getSessionId() || joinId;
  if (!sessionId) throw new Error('Visko connected without a session ID.');
  hubSend({ type: 'session', model: targetModel, sessionId, owner: joinId ? sharedSession?.owner || 'unity' : 'web' });
  return client;
}
async function begin() {
  if (busy || active || !lesson) return;
  voiceReconciling = voiceUnsafe = voiceCheckRunning = false;
  lessonOperations.reset(); idleInvitation.reset(); agentMachine.send('begin');
  lastMediaTime = 0; lastMediaProgressAt = -Infinity;
  busy = true; finished = false; preview = $('previewOnly').checked; beat = 0; imageRef = null; latestDecision = null; lastPromptAt = 0; availableResolutions = []; webResolution = 'native';
  runtimeEvents = []; runtimeState = emptyRuntimeState(); $('runtimeTrace').textContent = 'No live model activity.';
  const run = ++epoch; apiAbort = new AbortController(); refresh(); notify('Preparing your scene…');
  try {
    const photo = photoFile ? await preparePhoto(photoFile) : null; assertCurrent(run);
    if (preview) {
      sessionId = `practice-${crypto.randomUUID()}`; model = null;
      imageRef = photoFile ? { id: photoFile.name, caption: photoCaption } : null;
      $('sceneStatus').textContent = 'Practice · no live video';
    } else {
      const joinId = $('sessionId').value.trim();
      const requestedModel = MODELS[$('model').value];
      const client = requestedModel === MODELS.dynamic && !joinId
        ? await connectWithCapacityRetry({
          connect: () => { assertCurrent(run); return connectReactor(requestedModel, '', run); }, signal: apiAbort.signal,
          getCooldown: () => nextAllowedConnectAt, getReason: () => lastRateLimitReason,
          setCooldown: (until, reason) => { nextAllowedConnectAt = until; lastRateLimitReason = reason; },
          onWait: ({ seconds, attempt, maxAttempts, reason }) => {
            if (run !== epoch) return;
            $('retryStatus').hidden = false;
            $('retryStatus').textContent = `Dynamic ${reason === 'capacity' ? 'has no capacity' : 'is rate limited'} (429) · retry in ${seconds}s · attempt ${attempt}/${maxAttempts}. Leave cancels.`;
          },
        })
        : await connectReactor(requestedModel, joinId, run);
      $('retryStatus').hidden = true;
      assertCurrent(run);
      // Joining for a new lesson explicitly resets the model before applying its start photo.
      if (joinId) await command('reset', {}, client);
      await api('/api/agent/reset', { sessionId, lessonId: lesson.id }, apiAbort.signal); assertCurrent(run);
      if (photo) {
        const ref = await client.uploadFile(photo); assertCurrent(run);
        await command('set_image', { image: ref }, client); assertCurrent(run);
        imageRef = { id: ref.uploadId, caption: photoCaption };
      }
    }
    active = true; busy = false; setSetupOpen(false); refresh();
    if (!await turn({}, run)) throw new Error('The lesson guide could not start. Please try again.');
  } catch (error) {
    if (run === epoch) { await leave(); notify(error.name === 'AbortError' ? 'Lesson cancelled.' : `Could not start: ${error.message}`, error.name !== 'AbortError'); }
  } finally { if (run === epoch) { busy = false; refresh(); void reconcileVoice(); } }
}
function contextSnapshot(gesture = null) {
  return {
    gesture, pointedAt: gesture === 'point' ? $('pointedAt').value : null,
    roomLabels: [...document.querySelectorAll('input[name="room"]:checked')].map((input) => input.value),
    planes: { horizontalAtDeskHeight: $('deskPlane').checked }, gaze: $('gaze').value,
    rms: Number($('rms').value), glassesKey: null,
  };
}
async function setWebResolution() {
  // Web may use 1080p when offered by Reactor; glasses keep their native policy.
  let resolution = availableResolutions.length && !availableResolutions.includes('native') && availableResolutions.includes('1080p') ? '1080p' : webResolution;
  try { await command('set_resolution', { resolution }); }
  catch (error) {
    const reason = String(error.message || error);
    const advertisedFallback = /1080p/.test(reason) && /resolution/i.test(reason) && /not available|not supported|not offered|unsupported|invalid|available|offered/i.test(reason);
    if (resolution !== 'native' || !advertisedFallback) throw error;
    resolution = '1080p'; await command('set_resolution', { resolution });
  }
  webResolution = resolution; $('sceneStatus').textContent = `Live · ${webResolution}`;
  return { name: 'set_resolution', data: { resolution } };
}
async function executeDecision(decision, run) {
  if (preview) return [];
  const executed = [];
  const allowed = new Set(['set_prompt', 'set_audio_prompt', 'set_resolution', 'start', 'pause', 'resume', 'reset']);
  for (const item of decision.visko) {
    assertCurrent(run);
    if (!allowed.has(item.name)) continue; // Image refs are bound locally through uploadFile, never agent strings.
    if (item.name === 'set_prompt') {
      const delay = Math.max(0, 2000 - (Date.now() - lastPromptAt));
      if (delay) await new Promise((resolve) => setTimeout(resolve, delay));
      assertCurrent(run); lastPromptAt = Date.now();
    }
    if (item.name === 'set_resolution') { executed.push(await setWebResolution()); continue; }
    if (item.name === 'start') executed.push(await setWebResolution());
    await command(item.name, item.data || {}); executed.push({ name: item.name, data: item.data || {} });
  }
  return executed;
}
function renderDecision(decision, hasSpeech = false) {
  clerkSpeech = decision.speech?.role === 'clerk' && decision.speech.language === 'ja' && typeof decision.speech.text === 'string' && decision.speech.text.trim()
    ? { text: decision.speech.text, language: 'ja', role: 'clerk' } : null;
  $('clerkCaption').textContent = clerkSpeech ? `店员 · ${clerkSpeech.text}` : '';
  $('clerkCaption').hidden = !clerkSpeech; $('clerkReplay').hidden = !clerkSpeech;
  const overlay = decision.overlay;
  for (const field of ['jp', 'kana', 'romaji', 'zh', 'en']) $(field).textContent = overlay?.[field] || '';
  if (!overlay && decision.advance === 'scene_done') { $('jp').textContent = 'お疲れさまでした。'; $('en').textContent = 'Scene complete. Leave the session when you are ready.'; }
  $('hintLevel').hidden = !overlay?.hintLevel;
  $('hintLevel').textContent = overlay?.hintLevel ? `HINT ${overlay.hintLevel}` : '';
  $('feedback').textContent = decision.feedback ? `${hasSpeech ? `${decision.feedback.score}/100 · ` : ''}${(decision.feedback.notes || []).join(' ')}` : '';
  const total = lesson?.beats.reduce((n, b) => n + b.lines.length, 0) || 0;
  $('progress').textContent = decision.advance === 'scene_done' ? 'SCENE COMPLETE' : `LINE ${Math.min(beat + 1, total)} / ${total}`;
  const route = decision.route;
  const onDetour = route?.kind === 'detour';
  $('routeStatus').hidden = !onDetour && route?.kind !== 'rejoin';
  $('routeStatus').textContent = onDetour
    ? `支线 · ${route.label} ${route.step}/${route.total} → 主线 ${Number(route.returnBeat) + 1}`
    : route?.kind === 'rejoin' ? `回到主线 · ${route.label}` : '';
}
async function turn({ gesture = null, transcript, intent, action, fromVoice = false } = {}, run = epoch) {
  if (!active || busy || finished || voiceReconciling || voiceUnsafe || (voiceActive && !fromVoice) || !agentMachine.send('observe')) return;
  idleInvitation.suspend(); lessonSpeech.cancel();
  busy = true; refresh();
  const context = contextSnapshot(gesture);
  let request = { sessionId, lessonId: lesson.id, beat, imageRef, context, ...(transcript ? { transcript } : {}), ...(intent ? { intent } : {}), ...(action ? { action } : {}) };
  agentMachine.send('plan');
  try {
    const operation = await lessonOperations.turn(request, { signal: apiAbort.signal, isCurrent: () => run === epoch }); assertCurrent(run);
    request = operation.request;
    const decision = operation.decision;
    if (!Array.isArray(decision.visko) || !['stay', 'next_line', 'scene_done'].includes(decision.advance)) throw new Error('The lesson guide returned an invalid decision.');
    agentMachine.send('act');
    let executed;
    try {
      if (!operation.claimExecution()) return false;
      executed = await executeDecision(decision, run); assertCurrent(run);
    }
    catch (error) {
      if (run === epoch) { agentMachine.send('fail'); await leave(); notify(`Scene command failed: ${error.message}. Begin again to restart safely.`, true); }
      return false;
    }
    if (decision.advance === 'next_line') beat++;
    finished = decision.advance === 'scene_done'; latestDecision = decision;
    const execution = { mode: preview ? 'practice' : 'live', resolution: preview ? null : webResolution, visko: executed };
    renderDecision(decision, Boolean(transcript)); $('decision').textContent = JSON.stringify({ request, response: decision, execution }, null, 2);
    hubSend({ type: 'learn', sessionId, lessonId: lesson.id, beat, context, ...(transcript ? { transcript } : {}), ...(intent ? { intent } : {}), ...decision, execution });
    notify(finished ? (preview ? 'Practice complete. Press Leave to start again.' : 'Lesson complete. Press Leave to end the billed session.') : preview ? 'Practice mode: speak a line or try the glasses signals.' : 'Your scene is ready. Speak the Japanese line, or ask for a hint.');
    agentMachine.send('done', { complete: finished });
    if (clerkSpeech && !voiceActive) void lessonSpeech.play(clerkSpeech);
    return true;
  } catch (error) {
    if (run === epoch && error.name !== 'AbortError') { agentMachine.send('fail'); notify(error.status === 409 ? 'Lesson state changed or this request expired. Leave and restart safely; no scene commands were replayed.' : `Turn failed: ${error.message}`, true); }
    return false;
  }
  finally { if (run === epoch) { busy = false; refresh(); void reconcileVoice(); } }
}
function stopSpeech() {
  if (recognition) recognition.stop();
  else if (recording?.recorder.state === 'recording') recording.recorder.stop();
  else if (micStarting) { ++speechAttempt; micStarting = false; $('speechStatus').textContent = 'Microphone request cancelled.'; refresh(); }
}
function cancelSpeech() {
  ++speechAttempt; micStarting = false; transcribing = false;
  if (recognition) { recognition.onend = null; recognition.onerror = null; recognition.abort(); recognition = null; }
  if (recording) {
    const pending = recording; recording = null; pending.cancelled = true; clearTimeout(pending.timer);
    if (pending.recorder.state !== 'inactive') pending.recorder.stop();
    for (const track of pending.stream.getTracks()) track.stop();
  }
}
function microphoneError(error) {
  if (error?.name === 'NotAllowedError' || error?.name === 'SecurityError') return 'Microphone access was blocked. Allow microphone permission for this site, then press Speak again.';
  if (error?.name === 'NotFoundError') return 'No microphone was found. Connect one and try again.';
  if (error?.name === 'NotReadableError') return 'The microphone is unavailable or in use by another app. Close that app and retry.';
  return 'Could not record the microphone. Try again or choose another speech input in lesson setup.';
}
async function finishRecording(pending) {
  clearTimeout(pending.timer);
  for (const track of pending.stream.getTracks()) track.stop();
  if (recording === pending) recording = null;
  if (pending.cancelled || pending.run !== epoch || pending.attempt !== speechAttempt) return;
  const audio = new Blob(pending.chunks, { type: pending.recorder.mimeType || pending.chunks[0]?.type || 'audio/webm' });
  if (!audio.size) { notify('No audio was recorded. Press Speak and try again.', true); refresh(); return; }
  transcribing = true; $('speechStatus').textContent = 'Microphone stopped · transcribing…'; refresh();
  try {
    const response = await fetch('/api/agent/transcribe', { method: 'POST', headers: { ...authHeaders(), 'Content-Type': audio.type, 'x-audio-language': pending.language }, body: audio, signal: apiAbort.signal });
    if (!response.ok) {
      const errors = { 401: 'Portal authorization is required for transcription.', 413: 'Recording was too large. Try a shorter phrase.', 415: 'This audio format is unsupported. Try Chrome or another speech input.', 429: 'Speech service is busy. Wait a moment and try again.', 503: 'Server speech is not configured or available. Check the portal speech settings.', 504: 'Speech service timed out. Press Speak to retry.' };
      throw new Error(errors[response.status] || 'Server transcription failed. Press Speak to try again.');
    }
    const result = await response.json();
    if (pending.run !== epoch || pending.attempt !== speechAttempt) return;
    const text = typeof result.text === 'string' ? result.text.trim() : '';
    if (!text) throw new Error('No speech was detected. Press Speak and try again.');
    if (text.length > (pending.intentMode ? 500 : 1000)) throw new Error('That phrase was too long. Please record a shorter sentence.');
    $('transcript').textContent = text; transcribing = false; $('speechStatus').textContent = 'Microphone stopped.'; refresh();
    await turn(pending.intentMode ? { intent: text } : { transcript: text }, pending.run);
  } catch (error) {
    if (pending.run === epoch && pending.attempt === speechAttempt && error.name !== 'AbortError') notify(error instanceof TypeError ? 'Could not reach the transcription server. Your microphone permission is already granted; check the portal connection and retry.' : error.message, true);
  } finally {
    if (pending.run === epoch && pending.attempt === speechAttempt) { transcribing = false; $('speechStatus').textContent = 'Microphone stopped.'; refresh(); }
  }
}
async function startRecording() {
  const run = epoch, attempt = ++speechAttempt, intentMode = $('speechMode').value === 'intent';
  const language = intentMode ? $('intentLanguage').value : 'ja-JP';
  micStarting = true; $('speechStatus').textContent = 'Opening microphone…'; refresh();
  let stream;
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    if (run !== epoch || attempt !== speechAttempt) { stream.getTracks().forEach((track) => track.stop()); return; }
    const mimeType = ['audio/webm;codecs=opus', 'audio/mp4', 'audio/ogg;codecs=opus'].find((type) => MediaRecorder.isTypeSupported(type));
    const recorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined);
    const pending = { recorder, stream, chunks: [], bytes: 0, run, attempt, intentMode, language, cancelled: false, timer: null };
    recording = pending; micStarting = false;
    recorder.ondataavailable = (event) => {
      if (pending.cancelled || !event.data.size) return;
      pending.bytes += event.data.size;
      if (pending.bytes > 10 * 1024 * 1024) { pending.cancelled = true; recorder.stop(); notify('Recording was too large. Try a shorter phrase.', true); }
      else pending.chunks.push(event.data);
    };
    recorder.onerror = () => {
      pending.cancelled = true; clearTimeout(pending.timer); stream.getTracks().forEach((track) => track.stop());
      if (recording === pending) recording = null;
      if (run === epoch && attempt === speechAttempt) { notify('Audio recording failed. Press Speak to retry.', true); refresh(); }
    };
    recorder.onstop = () => { void finishRecording(pending); if (pending.cancelled && run === epoch && attempt === speechAttempt) { $('speechStatus').textContent = 'Microphone stopped.'; refresh(); } };
    recorder.start(250);
    pending.timer = setTimeout(() => { if (recorder.state === 'recording') recorder.stop(); }, 15000);
    $('speechStatus').textContent = `Recording ${language} · press Stop when finished · 15 seconds max.`;
    notify('Recording only your microphone. Press Stop to send it for transcription.'); refresh();
  } catch (error) {
    stream?.getTracks().forEach((track) => track.stop());
    if (run === epoch && attempt === speechAttempt) { recording = null; micStarting = false; notify(microphoneError(error), true); $('speechStatus').textContent = 'Microphone stopped.'; refresh(); }
  }
}
function startBrowserSpeech() {
  if (!SpeechRecognition) { $('speechEngine').value = 'server'; void startRecording(); return; }
  const speech = new SpeechRecognition(), run = epoch, attempt = ++speechAttempt, intentMode = $('speechMode').value === 'intent'; recognition = speech;
  speech.lang = intentMode ? $('intentLanguage').value : 'ja-JP'; speech.interimResults = true; speech.continuous = false; speech.maxAlternatives = 1;
  let finalText = '', failed = false;
  speech.onstart = () => { if (run !== epoch || attempt !== speechAttempt) return; $('speechStatus').textContent = intentMode ? `Tell the guide what you want to do · ${speech.lang}…` : 'Listening in Japanese…'; refresh(); };
  speech.onresult = (event) => {
    if (run !== epoch || attempt !== speechAttempt) return;
    let interim = '';
    for (let i = event.resultIndex; i < event.results.length; i++) {
      const text = event.results[i][0].transcript;
      if (event.results[i].isFinal) finalText += text; else interim += text;
    }
    $('transcript').textContent = finalText + interim;
  };
  speech.onerror = (event) => {
    if (run !== epoch || attempt !== speechAttempt) return;
    failed = true;
    if (event.error === 'network') {
      $('speechEngine').value = 'server';
      notify('The browser speech service could not connect. Switched to server speech: press Speak again, then Stop when finished. Microphone permission is not the problem.', true);
    } else if (event.error === 'not-allowed' || event.error === 'service-not-allowed') notify('Microphone or browser speech permission was blocked. Allow it for this site, or choose server speech in lesson setup.', true);
    else if (event.error === 'audio-capture') notify('No available microphone could be captured. Check the selected input device.', true);
    else if (event.error === 'no-speech') notify('No speech was detected. Press Speak to try again.', true);
    else if (event.error !== 'aborted') notify(`Browser speech failed (${event.error}). Try server speech in lesson setup.`, true);
  };
  speech.onend = () => {
    if (recognition === speech) recognition = null;
    if (run !== epoch || attempt !== speechAttempt) return;
    $('speechStatus').textContent = 'Microphone stopped.'; refresh();
    if (!failed && finalText.trim()) void turn(intentMode ? { intent: finalText.trim() } : { transcript: finalText.trim() }, run);
  };
  try { speech.start(); refresh(); } catch (error) { recognition = null; notify(microphoneError(error), true); refresh(); }
}
function startSpeech() {
  if (voiceActive || voiceReconciling || voiceUnsafe || recognition || recording || micStarting || transcribing || busy || !active || finished) return;
  idleInvitation.suspend(); lessonSpeech.cancel();
  if ($('speechEngine').value === 'browser') startBrowserSpeech(); else void startRecording();
}
async function leave(message = 'You have left the lesson.') {
  active = false; lineVoice.disconnect();
  voiceReconciling = voiceUnsafe = voiceCheckRunning = false;
  idleInvitation.reset(); agentMachine.send('stop');
  scenePoint.clear(); lessonSpeech.cancel(); lessonOperations.reset(); clerkSpeech = null;
  $('clerkCaption').hidden = true; $('clerkReplay').hidden = true;
  ++epoch; apiAbort?.abort(); $('retryStatus').hidden = true; const client = reactor, oldId = sessionId, wasPreview = preview;
  reactor = null; active = false; busy = false; sessionId = null; finished = false; latestDecision = null;
  cancelSpeech();
  if (oldId && !wasPreview) hubSend({ type: 'session_end', sessionId: oldId });
  for (const track of mediaStream.getTracks()) track.stop(); mediaStream = new MediaStream(); $('video').srcObject = null;
  $('video').muted = true; $('sound').textContent = 'Unmute scene'; $('photoPreview').hidden = !photoFile; $('sceneFocus').hidden = !photoFile;
  $('sceneStatus').textContent = 'Session ended'; $('speechStatus').textContent = 'Microphone stopped.';
  refresh(); notify(message);
  if (client) {
    try { await client.disconnect(); }
    catch (error) { notify(`Disconnect did not confirm: ${error.message}. Check the Reactor dashboard before leaving.`, true); }
  }
}
function setSetupOpen(open) {
  $('setupPanel').hidden = !open;
  $('setupToggle').setAttribute('aria-expanded', String(open));
}
function unload() {
  if (closing) return; closing = true; clearTimeout(hubRetry);
  // Initiate non-recoverable disconnect synchronously; page teardown cannot await it.
  void leave(); hub?.close(); if (previewUrl) URL.revokeObjectURL(previewUrl);
}

$('sample').append(...SAMPLES.map((sample) => new Option(sample.label, sample.file)));
$('lesson').onchange = () => void chooseLesson();
$('sample').onchange = async () => { busy = true; refresh(); try { await chooseSample(); notify('Start image selected.'); } catch (error) { notify(error.message, true); } finally { busy = false; refresh(); } };
$('photo').onchange = () => {
  const file = $('photo').files[0]; if (!file) return;
  if (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type) || file.size > 10 * 1024 * 1024) { notify('Choose a JPEG, PNG or WebP photo under 10 MB.', true); $('photo').value = ''; return; }
  setPhoto(file, 'Learner-selected start photo; its visual contents have not been captioned.'); $('sample').value = ''; notify('Your photo is ready.');
};
$('setupToggle').onclick = () => setSetupOpen($('setupPanel').hidden);
$('setupClose').onclick = () => { setSetupOpen(false); $('setupToggle').focus(); };
$('previewOnly').onchange = refresh;
$('speechEngine').onchange = () => { $('speechStatus').textContent = $('speechEngine').value === 'server' ? 'Press Speak, then Stop to transcribe (15 seconds max).' : 'Browser speech recognition selected.'; refresh(); };
$('speechMode').onchange = () => {
  refresh();
  $('speechStatus').textContent = $('speechMode').value === 'intent' ? 'Say what you want to do. The guide will lead you back to the lesson.' : 'Practice the Japanese line in the scene.';
};
$('translationToggle').onclick = () => {
  $('translations').hidden = !$('translations').hidden;
  $('translationToggle').setAttribute('aria-expanded', String(!$('translations').hidden));
};
$('begin').onclick = begin; $('leave').onclick = () => leave();
$('listen').onclick = () => { if (!$('listen').disabled) void lessonSpeech.play({ text: latestDecision.overlay.jp, language: 'ja', role: 'learner' }); };
$('clerkReplay').onclick = () => { if (!$('clerkReplay').disabled && clerkSpeech) void lessonSpeech.play(clerkSpeech); };
$('conversation').onclick = () => {
  if (voiceActive) lineVoice.disconnect();
  else if (active && !busy && !finished && !voiceReconciling && !voiceUnsafe) { cancelSpeech(); lessonSpeech.cancel(); idleInvitation.suspend(); scenePoint.clear(); void lineVoice.connect(); }
};
$('speak').onclick = startSpeech; $('stopSpeech').onclick = stopSpeech;
$('pinch').onclick = () => void turn({ gesture: 'pinch' });
$('returnMain').onclick = () => void turn({ action: 'return_to_main' });
$('longPinch').onclick = () => void turn({ gesture: 'long_pinch' });
$('point').onclick = () => void turn({ gesture: 'point' });
$('sendContext').onclick = () => void turn();
$('rms').oninput = () => { $('rmsValue').value = Number($('rms').value).toFixed(2); };
$('sound').onclick = () => { $('video').muted = !$('video').muted; $('sound').textContent = $('video').muted ? 'Unmute scene' : 'Mute scene'; void $('video').play().catch(() => {}); };
$('useShared').onclick = () => { if (sharedSession) { $('sessionId').value = sharedSession.sessionId; $('model').value = sharedSession.model === MODELS.dynamic ? 'dynamic' : 'stable'; } };
window.addEventListener('pagehide', unload); window.addEventListener('beforeunload', unload);
window.addEventListener('pageshow', (event) => { if (event.persisted) { closing = false; connectHub(); refresh(); } });
if (!window.MediaRecorder || !navigator.mediaDevices?.getUserMedia) $('speechStatus').textContent = 'Audio recording requires a supported browser on HTTPS or localhost.';
else if (!window.isSecureContext) $('speechStatus').textContent = 'Microphone access needs HTTPS or localhost. On a phone, use an HTTPS portal URL.';
const scenePoint = installScenePoint({
  ready: () => active && !voiceActive && !voiceReconciling && !voiceUnsafe && !busy && !finished && !recognition && !recording && !micStarting && !transcribing,
  getRun: () => `${epoch}:${beat}:${latestDecision?.route?.nodeId || ''}`,
  getSignal: () => apiAbort?.signal,
  headers: authHeaders,
  onIntent: intent => turn({ intent }),
  notify,
});
connectHub(); void loadCatalog(); refresh();

// Deliberate user activity postpones help; passive viewing can receive one invitation.
for (const event of ['pointerdown', 'keydown', 'input']) document.addEventListener(event, () => idleInvitation.activity(), { passive: true });
document.addEventListener('visibilitychange', () => idleInvitation.activity());
$('video').addEventListener('timeupdate', () => {
  const time = $('video').currentTime;
  if (time > lastMediaTime) lastMediaProgressAt = performance.now();
  lastMediaTime = time;
  idleInvitation.sync(latestDecision?.route?.nodeId || null);
});
for (const event of ['pause', 'ended', 'waiting', 'stalled']) $('video').addEventListener(event, () => { lastMediaProgressAt = -Infinity; idleInvitation.suspend(); });
