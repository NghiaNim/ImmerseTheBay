// ImmerseTheBay portal
//  - POST /api/token   : mints a session-scoped Reactor JWT (API key never leaves this process)
//  - GET  /api/models  : the two Visko models
//  - WS   /sync        : hub that keeps the web portal and the Unity app in sync
//                        (current session id + model, relayed commands, state broadcasts)
//  - static public/    : the quick-test web page
import 'dotenv/config';
import express from 'express';
import http from 'node:http';
import os from 'node:os';
import { WebSocketServer } from 'ws';
import { attachCast } from './cast/castHub.js';
import { registerAgentRoutes } from './agent/routes.js';
import { createAgentService } from './agent/service.js';
import { createLessonStore } from './agent/lessons.js';
import { registerPracticeRoutes } from './practice/routes.js';
import { registerVoiceRoutes } from './voice/routes.js';
import { registerVisionRoutes } from './vision/routes.js';

const PORT = Number(process.env.PORT || 8790);
const API_KEY = process.env.REACTOR_API_KEY || '';
const PORTAL_TOKEN = process.env.PORTAL_TOKEN || '';
const TOKEN_TTL = Math.min(Number(process.env.TOKEN_TTL_SECONDS || 21600), 21600);
const REACTOR_API = 'https://api.reactor.inc';

export const MODELS = {
  stable: 'reactor/visko-orbis-stable',
  dynamic: 'reactor/visko-orbis-dynamic',
};

if (!API_KEY || API_KEY.startsWith('rk_your')) {
  console.warn('[portal] REACTOR_API_KEY is not set. Copy portal/.env.example to portal/.env and add your key.');
}

const app = express();
const lessonStore = createLessonStore();
registerPracticeRoutes(app, { authorized, store: lessonStore });
registerVisionRoutes(app, { authorized });
app.use(express.json({ limit: '64kb' }));

function authorized(req) {
  if (!PORTAL_TOKEN) return true;
  return req.get('portal_token') === PORTAL_TOKEN || req.query.portal_token === PORTAL_TOKEN;
}

const lessonService = createAgentService({ store: lessonStore });
registerAgentRoutes(app, { authorized, store: lessonStore, service: lessonService });
registerVoiceRoutes(app, { authorized, snapshot: input => lessonService.snapshot(input) });

app.get('/api/models', (_req, res) => res.json(MODELS));

app.get('/api/health', (_req, res) => res.json({ ok: true, keyConfigured: Boolean(API_KEY && !API_KEY.startsWith('rk_your')) }));

// Body: { model: "reactor/visko-orbis-dynamic" | "stable" | "dynamic", sessionId?: string }
// With sessionId the token is bound to that existing session (needed to join/adopt it).
app.post('/api/token', async (req, res) => {
  if (!authorized(req)) return res.status(401).json({ error: 'portal_token required' });
  if (!API_KEY || API_KEY.startsWith('rk_your')) return res.status(500).json({ error: 'REACTOR_API_KEY not configured in portal/.env' });

  const model = MODELS[req.body?.model] || req.body?.model;
  if (!Object.values(MODELS).includes(model)) return res.status(400).json({ error: `model must be one of ${Object.values(MODELS).join(', ')}` });
  const sessionId = typeof req.body?.sessionId === 'string' && req.body.sessionId ? req.body.sessionId : null;

  const resources = { models: { match: [model] } };
  if (sessionId) resources.sessions = { bind: [sessionId] };
  const body = {
    expires_after: TOKEN_TTL,
    authorization_details: [{ type: 'session', resources }],
    constraints: { max_sessions: sessionId ? 1 : 5, max_session_duration_seconds: TOKEN_TTL },
  };

  try {
    const r = await fetch(`${REACTOR_API}/tokens`, {
      method: 'POST',
      headers: { 'Reactor-API-Key': API_KEY, 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const text = await r.text();
    if (!r.ok) {
      console.warn(`[portal] token mint failed ${r.status}: ${text}`);
      return res.status(r.status).json({ error: `Reactor /tokens ${r.status}`, detail: safeJson(text) });
    }
    const data = JSON.parse(text);
    console.log(`[portal] minted token model=${model} session=${sessionId ?? 'new'} expires_at=${data.expires_at}`);
    res.json({ jwt: data.jwt, expires_at: data.expires_at, model, sessionId });
  } catch (e) {
    console.error('[portal] token mint error', e);
    res.status(502).json({ error: String(e) });
  }
});

app.use(express.static(new URL('./public/', import.meta.url).pathname));

// ---------------------------------------------------------------- sync hub
// Messages (JSON): every message has "type"; the hub stamps "from" (client role) and "ts".
//   hello   {role: "web"|"unity", name?}         -> hub replies {type:"welcome", clients:[...], session}
//   session {model, sessionId, owner}             -> stored and broadcast; late joiners get it on welcome
//   session_end {}                                -> clears stored session, broadcast
//   command {name, data}                          -> relayed to the other clients (the session owner executes it)
//   state / status / log {...}                    -> relayed to the other clients
const server = http.createServer(app);
// noServer + explicit routing so /sync and /cast (first-person view relay) can share the HTTP server.
const wss = new WebSocketServer({ noServer: true, maxPayload: 1 << 20 });
server.on('upgrade', (req, socket, head) => {
  if (new URL(req.url, 'http://x').pathname !== '/sync') return; // other paths (e.g. /cast) handle themselves
  wss.handleUpgrade(req, socket, head, (ws) => wss.emit('connection', ws, req));
});
attachCast(server, { portalToken: PORTAL_TOKEN });
let currentSession = null; // {model, sessionId, owner, ts}
const clients = new Map(); // ws -> {role, name}

function broadcast(msg, except) {
  const data = JSON.stringify(msg);
  for (const ws of wss.clients) if (ws !== except && ws.readyState === 1) ws.send(data);
}

function roster() {
  return [...clients.values()].map((c) => ({ role: c.role, name: c.name }));
}

wss.on('connection', (ws, req) => {
  const url = new URL(req.url, 'http://x');
  if (PORTAL_TOKEN && url.searchParams.get('portal_token') !== PORTAL_TOKEN) {
    ws.close(4401, 'portal_token required');
    return;
  }
  clients.set(ws, { role: 'unknown', name: '' });

  ws.on('message', (raw) => {
    let msg;
    try { msg = JSON.parse(raw.toString()); } catch { return; }
    if (!msg || typeof msg.type !== 'string') return;
    const me = clients.get(ws);
    msg.from = me.role;
    msg.ts = Date.now();

    switch (msg.type) {
      case 'hello':
        me.role = msg.role === 'unity' ? 'unity' : 'web';
        me.name = String(msg.name || '');
        ws.send(JSON.stringify({ type: 'welcome', clients: roster(), session: currentSession, ts: Date.now() }));
        broadcast({ type: 'roster', clients: roster(), ts: Date.now() }, ws);
        console.log(`[sync] hello ${me.role} ${me.name}`);
        break;
      case 'session':
        currentSession = { model: msg.model, sessionId: msg.sessionId, owner: msg.owner || me.role, ts: msg.ts };
        console.log(`[sync] session ${currentSession.sessionId} model=${currentSession.model} owner=${currentSession.owner}`);
        broadcast({ type: 'session', ...currentSession, from: me.role }, ws);
        break;
      case 'session_end':
        currentSession = null;
        broadcast(msg, ws);
        break;
      case 'ping':
        ws.send(JSON.stringify({ type: 'pong', t: msg.t, ts: Date.now() }));
        break;
      default: // command, state, status, log, ...
        broadcast(msg, ws);
    }
  });

  ws.on('close', () => {
    clients.delete(ws);
    broadcast({ type: 'roster', clients: roster(), ts: Date.now() });
  });
});

function safeJson(t) { try { return JSON.parse(t); } catch { return t; } }

function lanAddresses() {
  return Object.values(os.networkInterfaces()).flat().filter((i) => i && i.family === 'IPv4' && !i.internal).map((i) => i.address);
}

server.listen(PORT, '0.0.0.0', () => {
  console.log(`[portal] http://localhost:${PORT}`);
  for (const ip of lanAddresses()) console.log(`[portal] LAN: http://${ip}:${PORT}  (put this in Assets/StreamingAssets/visko.json -> portalUrl)`);
});
