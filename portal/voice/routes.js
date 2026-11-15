// Server-created Cartesia sessions keep API credentials and trusted lesson binding off the browser.
export const CARTESIA_AGENT_VERSION = '2026-08-14';
const API = 'https://api.cartesia.ai/v1/agents/websocket/';
export function voiceConfig(env = process.env) {
  const agentId = (env.CARTESIA_AGENT_ID || '').trim();
  const apiKey = (env.CARTESIA_API_KEY || '').trim();
  return { agentId, apiKey, configured: Boolean(apiKey && /^agent_[a-zA-Z0-9_-]+$/.test(agentId)) };
}
export function registerVoiceRoutes(app, { authorized = () => true, snapshot, env = process.env, fetchImpl = fetch } = {}) {
  const protect = (req, res, next) => {
    res.set('Cache-Control', 'no-store');
    return authorized(req) ? next() : res.status(401).json({ error: 'portal_token required' });
  };
  app.get('/api/voice/config', protect, (_req, res) => {
    const { configured, agentId } = voiceConfig(env);
    res.json({ configured, provider: 'cartesia', agentId: agentId || null });
  });
  app.post('/api/voice/token', protect, async (req, res) => {
    const { configured, apiKey, agentId } = voiceConfig(env);
    if (!configured) return res.status(503).json({ error: 'Configure CARTESIA_API_KEY and CARTESIA_AGENT_ID in portal/.env' });
    const input = req.body;
    if (!input || typeof input !== 'object' || Array.isArray(input) || Object.keys(input).some(key => !['sessionId', 'lessonId'].includes(key)) || typeof input.sessionId !== 'string' || !/^[a-zA-Z0-9_.:-]{1,160}$/.test(input.sessionId) || typeof input.lessonId !== 'string' || !/^[a-z0-9][a-z0-9-]{0,79}$/.test(input.lessonId)) {
      return res.status(400).json({ error: 'Invalid voice session request' });
    }
    try {
      const state = snapshot(input);
      if (!state || !state.response || state.response.advance === 'scene_done') return res.status(409).json({ error: 'Start an active lesson before connecting voice' });
      const scene = String(state.lessonContext?.scene || '').slice(0, 1000);
      const binding = { ...input, runId: state.runId, revision: String(state.revision), beat: String(state.beat), language: 'ja',
        practiceContext: JSON.stringify({ scene, goal: String(state.lessonContext?.goal || '').slice(0,500), learnerLine: state.response.overlay?.jp || '' }),
        opening: /konbini|convenience.store/i.test(input.lessonId + scene) ? 'いらっしゃいませ。何をお探しですか？' : 'こんにちは。一緒に練習しましょう。' };
      const upstream = await fetchImpl(`${API}${encodeURIComponent(agentId)}/sessions`, {
        method: 'POST', headers: { 'X-API-Key': apiKey, 'Cartesia-Version': CARTESIA_AGENT_VERSION, 'Content-Type': 'application/json' },
        body: JSON.stringify({ dynamic_variables: binding }), signal: AbortSignal.timeout(15000),
      });
      if (!upstream.ok) return res.status(upstream.status === 429 ? 429 : 502).json({ error: 'Cartesia voice session unavailable' });
      const session = await upstream.json();
      const url = new URL(session.url);
      if (url.protocol !== 'wss:' || !['api.cartesia.ai', 'api-us.cartesia.ai', 'api-eu.cartesia.ai'].includes(url.hostname) || url.username || url.password || url.search || url.hash || !url.pathname.startsWith('/v1/agents/websocket/') || typeof session.token !== 'string' || !session.token || typeof session.call_id !== 'string') throw new Error('Invalid provider session');
      // A reset or another turn during the network request invalidates this bootstrap.
      const latest = snapshot(input);
      if (!latest || latest.runId !== state.runId || latest.revision !== state.revision) return res.status(409).json({ error: 'Lesson changed while connecting voice; reconnect' });
      return res.json({ url: url.href, token: session.token, callId: session.call_id, agentId, version: CARTESIA_AGENT_VERSION, runId: state.runId, revision: state.revision });
    } catch (error) {
      if (error.status === 409) return res.status(409).json({ error: 'Lesson state is busy; retry voice connection' });
      return res.status(502).json({ error: 'Cartesia voice session unavailable' });
    }
  });
}
