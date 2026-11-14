// Visko quick-test portal. Talks to Reactor directly with the official JS SDK (WebRTC) and to the
// portal's /sync hub so the web page and the Unity app share one Reactor session (same video stream).
import { Reactor } from 'https://esm.sh/@reactor-team/js-sdk@3.4.0';

const $ = (id) => document.getElementById(id);
const MODELS = { stable: 'reactor/visko-orbis-stable', dynamic: 'reactor/visko-orbis-dynamic' };
const portalToken = new URLSearchParams(location.search).get('portal_token') || '';

let reactor = null;          // current Reactor client
let currentModel = null;     // full slug
let currentSessionId = null;
let ownsSession = false;
let unitySession = null;     // last session announced by Unity via the hub
let hub = null;

// ---------------------------------------------------------------- logging / UI
function log(...a) {
  const line = `${new Date().toLocaleTimeString()} ${a.map((x) => (typeof x === 'string' ? x : JSON.stringify(x))).join(' ')}`;
  const el = $('log');
  el.textContent = (line + '\n' + el.textContent).slice(0, 20000);
}
function pill(id, text, cls) { const p = $(id); p.textContent = text; p.className = 'pill' + (cls ? ' ' + cls : ''); }
function refreshButtons() {
  const connected = Boolean(reactor);
  $('create').disabled = connected;
  $('join').disabled = connected || !unitySession;
  $('leave').disabled = !connected;
  $('sessionInfo').textContent = currentSessionId
    ? `${currentModel} · session ${currentSessionId} · ${ownsSession ? 'owner' : 'joined'}`
    : unitySession ? `Unity session available: ${unitySession.sessionId} (${unitySession.model})` : 'no session';
}

// ---------------------------------------------------------------- portal API
async function mintToken(model, sessionId) {
  const r = await fetch('/api/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...(portalToken ? { portal_token: portalToken } : {}) },
    body: JSON.stringify({ model, sessionId }),
  });
  const j = await r.json();
  if (!r.ok) throw new Error(j.error + (j.detail ? ' ' + JSON.stringify(j.detail) : ''));
  return j.jwt;
}

// ---------------------------------------------------------------- sync hub
function connectHub() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  hub = new WebSocket(`${proto}://${location.host}/sync${portalToken ? `?portal_token=${encodeURIComponent(portalToken)}` : ''}`);
  hub.onopen = () => { pill('hubPill', 'sync: online', 'ok'); hubSend({ type: 'hello', role: 'web', name: navigator.userAgent.slice(0, 40) }); };
  hub.onclose = () => { pill('hubPill', 'sync: offline', 'err'); setTimeout(connectHub, 2000); };
  hub.onmessage = (ev) => {
    const msg = JSON.parse(ev.data);
    switch (msg.type) {
      case 'welcome':
        updateRoster(msg.clients);
        if (msg.session && msg.session.owner === 'unity') announceUnitySession(msg.session);
        break;
      case 'roster': updateRoster(msg.clients); break;
      case 'session':
        if (msg.from === 'unity' || msg.owner === 'unity') announceUnitySession(msg);
        break;
      case 'session_end':
        if (msg.from === 'unity') { unitySession = null; log('Unity ended its session'); refreshButtons(); }
        break;
      case 'status': if (msg.from === 'unity') pill('unityPill', `unity: ${msg.status}`, msg.status === 'READY' ? 'ok' : 'warn'); break;
      case 'state': if (msg.from === 'unity' && !reactor) showState(msg.data); break;
      case 'command':
        // Unity asks the web page to run a command (web owns the session)
        if (msg.from === 'unity' && reactor) runCommand(msg.name, msg.data || {}, { relay: false });
        break;
      case 'log': log('[unity]', msg.text); break;
    }
  };
}
function hubSend(msg) { if (hub && hub.readyState === 1) hub.send(JSON.stringify(msg)); }
function updateRoster(clients) {
  const unity = clients.find((c) => c.role === 'unity');
  pill('unityPill', unity ? `unity: online${unity.name ? ' · ' + unity.name : ''}` : 'unity: offline', unity ? 'ok' : '');
}
function announceUnitySession(s) {
  unitySession = { model: s.model, sessionId: s.sessionId };
  log('Unity session announced', unitySession);
  refreshButtons();
}

// ---------------------------------------------------------------- Reactor
async function connect({ model, sessionId }) {
  const jwt = await mintToken(model, sessionId);
  reactor = new Reactor({ modelName: model });
  currentModel = model;
  ownsSession = !sessionId;

  reactor.on('statusChanged', (status) => {
    pill('rtcPill', `reactor: ${status}`, status === 'ready' ? 'ok' : status === 'disconnected' ? '' : 'warn');
    if (status === 'ready') {
      currentSessionId = reactor.getSessionId();
      log('ready, session', currentSessionId);
      if (ownsSession) {
        hubSend({ type: 'session', model, sessionId: currentSessionId, owner: 'web' });
        // Shared session: default to native so the glasses can keep up (frames cross JNI per frame).
        if ($('resolution').value) runCommand('set_resolution', { resolution: $('resolution').value }, { relay: false });
      }
      refreshButtons();
    }
    if (status === 'disconnected') cleanup();
  });
  reactor.on('trackReceived', (name, track, stream) => {
    log('track', name, track.kind);
    if (track.kind === 'video') $('video').srcObject = stream;
    else if (track.kind === 'audio') {
      const v = $('video');
      const s = v.srcObject instanceof MediaStream ? v.srcObject : new MediaStream();
      s.addTrack(track); v.srcObject = s;
    }
  });
  reactor.on('message', (msg) => onModelMessage(msg));
  reactor.on('error', (e) => log('error', e?.message || e));

  log(sessionId ? `joining ${sessionId}` : `creating session on ${model}`);
  await reactor.connect(jwt, sessionId ? { sessionId } : undefined);
  refreshButtons();
}

function onModelMessage(msg) {
  const type = msg?.type; const data = msg?.data;
  if (type === 'state') { showState(data); hubSend({ type: 'state', data }); return; }
  if (type === 'chunk_complete') { pill('genPill', `chunk ${data.chunk_index}${data.active_prompt ? ' · ' + data.active_prompt.slice(0, 30) : ''}`, 'ok'); return; }
  log('←', type, data ?? '');
  if (type === 'generation_started') pill('genPill', `generating ${data?.fps ?? ''}fps`, 'ok');
  if (type === 'generation_paused') pill('genPill', 'paused', 'warn');
  if (type === 'generation_complete' || type === 'generation_reset') pill('genPill', 'idle');
  if (type === 'command_error') pill('genPill', `error: ${data?.reason}`, 'err');
}

function showState(s) {
  if (!s) return;
  $('stateView').textContent = 'state: ' + JSON.stringify(s);
  const sel = $('resolution');
  if (Array.isArray(s.available_resolutions)) {
    const have = new Set([...sel.options].map((o) => o.value));
    for (const r of s.available_resolutions) if (!have.has(r)) sel.add(new Option(r, r));
  }
}

async function runCommand(name, data = {}, { relay = true } = {}) {
  if ($('viaUnity').checked && relay) {
    hubSend({ type: 'command', name, data });
    log('→ unity', name, data);
    return;
  }
  if (!reactor) { log('not connected'); return; }
  log('→', name, data);
  try {
    const reply = await reactor.sendCommand(name, data);
    if (reply && reply.type) log('↩', reply.type, reply.data ?? '');
  } catch (e) { log('command failed', name, e?.message || e); }
}

async function leave() {
  if (!reactor) return;
  try { await reactor.disconnect(); } catch {}
  if (ownsSession) hubSend({ type: 'session_end' });
  cleanup();
}
function cleanup() {
  reactor = null; currentSessionId = null; ownsSession = false;
  $('video').srcObject = null;
  pill('rtcPill', 'reactor: disconnected'); pill('genPill', 'idle');
  refreshButtons();
}

// ---------------------------------------------------------------- wire UI
async function createWithRetry(attempts = 3) {
  for (let i = 1; i <= attempts; i++) {
    try { await connect({ model: MODELS[$('model').value] }); return; }
    catch (e) {
      cleanup();
      const capacity = /429|capacity/i.test(e.message);
      log(`connect failed (${i}/${attempts})`, e.message);
      if (!capacity || i === attempts) { if (capacity) log('Reactor has no capacity for this model right now; try again later or switch to stable.'); return; }
      await new Promise((r) => setTimeout(r, 5000));
    }
  }
}
$('create').onclick = () => createWithRetry();
$('join').onclick = () => unitySession && connect({ model: unitySession.model, sessionId: unitySession.sessionId }).catch((e) => { log('join failed', e.message); cleanup(); });
$('leave').onclick = leave;
$('setPrompt').onclick = () => runCommand('set_prompt', { prompt: $('prompt').value.trim() });
// Everything runs at native 832x480 (glasses-friendly); resolution applies at the next start, so send it every time.
$('start').onclick = async () => {
  await runCommand('set_resolution', { resolution: $('resolution').value || 'native' });
  await runCommand('set_prompt', { prompt: $('prompt').value.trim() });
  await runCommand('start');
};
$('pause').onclick = () => runCommand('pause');
$('resume').onclick = () => runCommand('resume');
$('reset').onclick = () => runCommand('reset');
$('applySettings').onclick = async () => {
  await runCommand('set_audio_prompt', { prompt: $('audioPrompt').value.trim() });
  if ($('resolution').value) await runCommand('set_resolution', { resolution: $('resolution').value });
  await runCommand('set_seed', { seed: Math.max(0, parseInt($('seed').value, 10) || 0) });
};
$('unmute').onclick = () => { const v = $('video'); v.muted = !v.muted; $('unmute').textContent = v.muted ? 'Unmute' : 'Mute'; };
window.addEventListener('beforeunload', () => { if (reactor) reactor.disconnect(); });

connectHub();
refreshButtons();
