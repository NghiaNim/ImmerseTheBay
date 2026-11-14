// SDK 3.4.0 signatures verified against its published declarations and Reactor's
// reactor-class / Visko stable schema docs. sendCommand reports errors via replies/events.
const $ = (id) => document.getElementById(id);
const MODELS = { stable: 'reactor/visko-orbis-stable', dynamic: 'reactor/visko-orbis-dynamic' };
const SAMPLES = [
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
let mediaStream = new MediaStream();

if (token) $('homeLink').href = `/?portal_token=${encodeURIComponent(token)}`;

function notify(text, error = false) { $('notice').textContent = text; $('notice').classList.toggle('error', error); }
function refresh() {
  $('begin').disabled = busy || active || !lesson || !photoFile;
  $('begin').textContent = $('previewOnly').checked ? 'Start practice' : 'Start live lesson';
  $('leave').disabled = !active && !busy && !reactor;
  for (const id of ['lesson', 'sample', 'photo', 'model', 'sessionId', 'previewOnly']) $(id).disabled = busy || active;
  $('useShared').disabled = busy || active || !sharedSession;
  for (const id of ['pinch', 'longPinch', 'point', 'sendContext']) $(id).disabled = !active || busy || finished;
  $('speak').disabled = !active || busy || finished || !SpeechRecognition || !!recognition;
  $('stopSpeech').disabled = !recognition;
  $('stopSpeech').hidden = !recognition;
  $('sound').disabled = !reactor;
  $('sessionInfo').textContent = sessionId ? `${preview ? 'Practice' : model} · ${sessionId}` : sharedSession ? `Shared session available: ${sharedSession.sessionId}` : '';
}
function authHeaders() { return token ? { portal_token: token } : {}; }
async function api(path, body, signal) {
  const response = await fetch(path, { method: body === undefined ? 'GET' : 'POST', headers: { ...authHeaders(), ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) }, body: body === undefined ? undefined : JSON.stringify(body), signal });
  const data = await response.json();
  if (!response.ok) throw new Error(data.error || `Portal returned ${response.status}`);
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
    await chooseLesson();
  } catch (error) { notify(`Could not load lessons: ${error.message}`, true); }
}
async function chooseLesson() {
  busy = true; refresh();
  try {
    lesson = await api(`/api/agent/lessons/${encodeURIComponent($('lesson').value)}`);
    const count = lesson.beats.reduce((n, b) => n + b.lines.length, 0);
    $('lessonInfo').textContent = `${lesson.level} · ${lesson.beats.length} moments · ${count} spoken lines`;
    const filename = String(lesson.startImage || '').split('/').pop();
    if (SAMPLES.some((sample) => sample.file === filename)) { $('sample').value = filename; await chooseSample(); }
  } catch (error) { lesson = null; notify(error.message, true); }
  finally { busy = false; refresh(); }
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
  if (!sample) return;
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
  if (reply?.type === 'command_error') throw new Error(reply.data?.reason || `${name} was rejected.`);
  if (lastSdkError) throw new Error(lastSdkError.message || String(lastSdkError));
  return reply;
}
async function connectReactor(targetModel, joinId, run) {
  const credentials = await api('/api/token', { model: targetModel, ...(joinId ? { sessionId: joinId } : {}) }, apiAbort.signal);
  assertCurrent(run);
  sdkPromise ||= import('https://esm.sh/@reactor-team/js-sdk@3.4.0');
  const { Reactor } = await sdkPromise; assertCurrent(run);
  const client = new Reactor({ modelName: targetModel }); reactor = client; model = targetModel;
  client.on('error', (error) => { if (client === reactor) lastSdkError = error; });
  client.on('statusChanged', (status) => {
    if (client !== reactor || run !== epoch) return;
    $('sceneStatus').textContent = `Visko ${status}`;
    if (status === 'disconnected' && active) void leave('Disconnected from Visko. Begin a new lesson to reconnect.');
  });
  client.on('trackReceived', (_name, track) => {
    if (client !== reactor || run !== epoch) { track.stop(); return; }
    for (const previous of mediaStream.getTracks().filter((t) => t.kind === track.kind)) { mediaStream.removeTrack(previous); previous.stop(); }
    mediaStream.addTrack(track); $('video').srcObject = mediaStream;
    if (track.kind === 'video') { $('photoPreview').hidden = false; $('sceneFocus').hidden = true; $('scenePlaceholder').hidden = true; }
    void $('video').play().catch(() => notify('Press Unmute scene to enable playback.'));
  });
  client.on('message', (msg) => {
    if (client !== reactor || run !== epoch) return;
    if (msg.type === 'chunk_complete') $('sceneStatus').textContent = `Live · ${webResolution} · chunk ${msg.data?.chunk_index ?? ''}`;
    if (msg.type === 'command_error') { lastSdkError = new Error(msg.data?.reason || 'Scene command rejected'); notify(lastSdkError.message, true); }
    if (msg.type === 'state') {
      if (Array.isArray(msg.data?.available_resolutions)) availableResolutions = msg.data.available_resolutions;
      hubSend({ type: 'state', sessionId, data: msg.data });
    }
  });
  try { await client.connect(credentials.jwt, joinId ? { sessionId: joinId } : undefined); assertCurrent(run); }
  catch (error) { if (reactor === client) reactor = null; await client.disconnect().catch(() => {}); throw error; }
  sessionId = client.getSessionId() || joinId;
  if (!sessionId) throw new Error('Visko connected without a session ID.');
  hubSend({ type: 'session', model: targetModel, sessionId, owner: joinId ? sharedSession?.owner || 'unity' : 'web' });
  return client;
}
async function begin() {
  if (busy || active || !lesson || !photoFile) return;
  busy = true; finished = false; preview = $('previewOnly').checked; beat = 0; imageRef = null; latestDecision = null; lastPromptAt = 0; availableResolutions = []; webResolution = 'native';
  const run = ++epoch; apiAbort = new AbortController(); refresh(); notify('Preparing your scene…');
  try {
    const photo = await preparePhoto(photoFile); assertCurrent(run);
    if (preview) {
      sessionId = `practice-${crypto.randomUUID()}`; model = null;
      imageRef = { id: photoFile.name, caption: photoCaption };
      $('sceneStatus').textContent = 'Practice · no live video';
    } else {
      const joinId = $('sessionId').value.trim();
      const requestedModel = MODELS[$('model').value];
      let client;
      try { client = await connectReactor(requestedModel, joinId, run); }
      catch (error) {
        assertCurrent(run);
        if (!joinId && requestedModel === MODELS.dynamic && /429|capacity/i.test(error.message)) {
          notify('Dynamic is at capacity. Connecting to Stable…'); $('model').value = 'stable';
          client = await connectReactor(MODELS.stable, '', run);
        } else throw error;
      }
      assertCurrent(run);
      // Joining for a new lesson explicitly resets the model before applying its start photo.
      if (joinId) await command('reset', {}, client);
      await api('/api/agent/reset', { sessionId, lessonId: lesson.id }, apiAbort.signal); assertCurrent(run);
      const ref = await client.uploadFile(photo); assertCurrent(run);
      await command('set_image', { image: ref }, client); assertCurrent(run);
      imageRef = { id: ref.uploadId, caption: photoCaption };
    }
    active = true; busy = false; setSetupOpen(false); refresh();
    if (!await turn({}, run)) throw new Error('The lesson guide could not start. Please try again.');
  } catch (error) {
    if (run === epoch) { await leave(); notify(error.name === 'AbortError' ? 'Lesson cancelled.' : `Could not start: ${error.message}`, error.name !== 'AbortError'); }
  } finally { if (run === epoch) { busy = false; refresh(); } }
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
  const overlay = decision.overlay;
  for (const field of ['jp', 'kana', 'romaji', 'zh', 'en']) $(field).textContent = overlay?.[field] || '';
  if (!overlay && decision.advance === 'scene_done') { $('jp').textContent = 'お疲れさまでした。'; $('en').textContent = 'Scene complete. Leave the session when you are ready.'; }
  $('hintLevel').hidden = !overlay?.hintLevel;
  $('hintLevel').textContent = overlay?.hintLevel ? `HINT ${overlay.hintLevel}` : '';
  $('feedback').textContent = decision.feedback ? `${hasSpeech ? `${decision.feedback.score}/100 · ` : ''}${(decision.feedback.notes || []).join(' ')}` : '';
  const total = lesson?.beats.reduce((n, b) => n + b.lines.length, 0) || 0;
  $('progress').textContent = decision.advance === 'scene_done' ? 'SCENE COMPLETE' : `LINE ${Math.min(beat + 1, total)} / ${total}`;
}
async function turn({ gesture = null, transcript } = {}, run = epoch) {
  if (!active || busy || finished) return;
  busy = true; refresh();
  const context = contextSnapshot(gesture);
  const request = { sessionId, lessonId: lesson.id, beat, imageRef, context, ...(transcript ? { transcript } : {}) };
  try {
    const decision = await api('/api/agent/turn', request, apiAbort.signal); assertCurrent(run);
    if (!Array.isArray(decision.visko) || !['stay', 'next_line', 'scene_done'].includes(decision.advance)) throw new Error('The lesson guide returned an invalid decision.');
    let executed;
    try { executed = await executeDecision(decision, run); assertCurrent(run); }
    catch (error) {
      if (run === epoch) { await leave(); notify(`Scene command failed: ${error.message}. Begin again to restart safely.`, true); }
      return false;
    }
    if (decision.advance === 'next_line') beat++;
    finished = decision.advance === 'scene_done'; latestDecision = decision;
    const execution = { mode: preview ? 'practice' : 'live', resolution: preview ? null : webResolution, visko: executed };
    renderDecision(decision, Boolean(transcript)); $('decision').textContent = JSON.stringify({ request, response: decision, execution }, null, 2);
    hubSend({ type: 'learn', sessionId, lessonId: lesson.id, beat, context, ...(transcript ? { transcript } : {}), ...decision, execution });
    notify(finished ? (preview ? 'Practice complete. Press Leave to start again.' : 'Lesson complete. Press Leave to end the billed session.') : preview ? 'Practice mode: speak a line or try the glasses signals.' : 'Your scene is ready. Speak the Japanese line, or ask for a hint.');
    return true;
  } catch (error) { if (run === epoch && error.name !== 'AbortError') notify(`Turn failed: ${error.message}`, true); return false; }
  finally { if (run === epoch) { busy = false; refresh(); } }
}
function stopSpeech() { if (recognition) recognition.stop(); }
function startSpeech() {
  if (!SpeechRecognition || recognition || busy || !active) return;
  const speech = new SpeechRecognition(), run = epoch; recognition = speech;
  speech.lang = 'ja-JP'; speech.interimResults = true; speech.continuous = false; speech.maxAlternatives = 1;
  let finalText = '';
  speech.onstart = () => { $('speechStatus').textContent = 'Listening in Japanese…'; refresh(); };
  speech.onresult = (event) => {
    if (run !== epoch) return;
    let interim = '';
    for (let i = event.resultIndex; i < event.results.length; i++) {
      const text = event.results[i][0].transcript;
      if (event.results[i].isFinal) finalText += text; else interim += text;
    }
    $('transcript').textContent = finalText + interim;
  };
  speech.onerror = (event) => { if (run === epoch) notify(`Speech recognition: ${event.error}. Check microphone access and try again.`, true); };
  speech.onend = () => {
    if (recognition === speech) recognition = null;
    $('speechStatus').textContent = 'Microphone stopped.'; refresh();
    if (run === epoch && finalText.trim()) void turn({ transcript: finalText.trim() }, run);
  };
  try { speech.start(); refresh(); } catch (error) { recognition = null; notify(error.message, true); refresh(); }
}
async function leave(message = 'You have left the lesson.') {
  ++epoch; apiAbort?.abort(); const client = reactor, oldId = sessionId, wasPreview = preview;
  reactor = null; active = false; busy = false; sessionId = null; finished = false; latestDecision = null;
  if (recognition) { recognition.onend = null; recognition.abort(); recognition = null; }
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
$('sample').onchange = async () => { busy = true; refresh(); try { await chooseSample(); notify('Start photo selected.'); } catch (error) { notify(error.message, true); } finally { busy = false; refresh(); } };
$('photo').onchange = () => {
  const file = $('photo').files[0]; if (!file) return;
  if (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type) || file.size > 10 * 1024 * 1024) { notify('Choose a JPEG, PNG or WebP photo under 10 MB.', true); $('photo').value = ''; return; }
  setPhoto(file, 'Learner-selected start photo; its visual contents have not been captioned.'); $('sample').value = ''; notify('Your photo is ready.');
};
$('setupToggle').onclick = () => setSetupOpen($('setupPanel').hidden);
$('setupClose').onclick = () => { setSetupOpen(false); $('setupToggle').focus(); };
$('previewOnly').onchange = refresh;
$('translationToggle').onclick = () => {
  $('translations').hidden = !$('translations').hidden;
  $('translationToggle').setAttribute('aria-expanded', String(!$('translations').hidden));
};
$('begin').onclick = begin; $('leave').onclick = () => void leave();
$('speak').onclick = startSpeech; $('stopSpeech').onclick = stopSpeech;
$('pinch').onclick = () => void turn({ gesture: 'pinch' });
$('longPinch').onclick = () => void turn({ gesture: 'long_pinch' });
$('point').onclick = () => void turn({ gesture: 'point' });
$('sendContext').onclick = () => void turn();
$('rms').oninput = () => { $('rmsValue').value = Number($('rms').value).toFixed(2); };
$('sound').onclick = () => { $('video').muted = !$('video').muted; $('sound').textContent = $('video').muted ? 'Unmute scene' : 'Mute scene'; void $('video').play().catch(() => {}); };
$('useShared').onclick = () => { if (sharedSession) { $('sessionId').value = sharedSession.sessionId; $('model').value = sharedSession.model === MODELS.dynamic ? 'dynamic' : 'stable'; } };
window.addEventListener('pagehide', unload); window.addEventListener('beforeunload', unload);
window.addEventListener('pageshow', (event) => { if (event.persisted) { closing = false; connectHub(); refresh(); } });
if (!SpeechRecognition) $('speechStatus').textContent = 'Speech recognition is unavailable in this browser. Use Chrome on localhost or HTTPS, or use Pinch to continue.';
else if (!window.isSecureContext) $('speechStatus').textContent = 'Microphone access needs HTTPS or localhost. On a phone, use an HTTPS portal URL.';
connectHub(); void loadCatalog(); refresh();
