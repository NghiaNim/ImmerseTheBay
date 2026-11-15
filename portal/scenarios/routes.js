import { LEVELS, createScenarioGenerator } from './generate.js';
import { LOCAL_USER } from '../records/store.js';
const PHOTO_REF = /^photo:(ph_[a-f0-9]{16})$/;
const SAMPLE_REF = /^sample:([a-zA-Z0-9][a-zA-Z0-9_-]*\.(?:jpg|jpeg|png|webp))$/i;
const LANGUAGES = ['zh-CN', 'en-US', 'ja-JP'];

/** Scenario creation from the learner's own request. Generated lessons run through /api/agent/turn as `gen-…`. */
export function registerScenarioRoutes(app, { authorized = () => true, store, generator = createScenarioGenerator(), userOf = () => LOCAL_USER, maxConcurrent = 2 } = {}) {
  const protect = (req, res, next) => { res.set('Cache-Control', 'no-store'); return authorized(req) ? next() : res.status(401).json({ error: 'portal_token required' }); };
  const route = (fn) => async (req, res) => {
    try { await fn(req, res, userOf(req)); }
    catch (e) { res.status(e.status || 500).json({ error: e.status ? e.message : 'Scenario service unavailable' }); if (!e.status) console.error('[scenarios]', e); }
  };
  let inFlight = 0;
  app.get('/api/scenarios/config', protect, (_req, res) => res.json({ configured: generator.configured, levels: LEVELS }));
  app.post('/api/scenarios', protect, route(async (req, res, userId) => {
    const body = req.body;
    if (!body || typeof body !== 'object' || Array.isArray(body) || Object.keys(body).some((k) => !['request', 'level', 'image', 'language'].includes(k))) return res.status(400).json({ error: 'Send {request, level, image?, language?}' });
    const request = typeof body.request === 'string' ? body.request.replace(/\s+/g, ' ').trim() : '';
    if (request.length < 3 || request.length > 500) return res.status(400).json({ error: 'Describe the situation in 3 to 500 characters' });
    if (!LEVELS.includes(body.level)) return res.status(400).json({ error: `level must be one of ${LEVELS.join(', ')}` });
    if (body.language !== undefined && !LANGUAGES.includes(body.language)) return res.status(400).json({ error: 'Unsupported request language' });
    let startImage = '';
    if (body.image !== undefined && body.image !== null && body.image !== '') {
      const photo = PHOTO_REF.exec(body.image)?.[1], sample = SAMPLE_REF.exec(body.image)?.[1];
      if (photo) { await store.getPhoto(userId, photo); startImage = `photos/${photo}`; }
      else if (sample) startImage = `lessons/images/${sample}`;
      else return res.status(400).json({ error: 'image must be photo:<id> or sample:<file>' });
    }
    if (inFlight >= maxConcurrent) return res.status(429).json({ error: 'A scenario is already being written. Wait for it to finish.' });
    inFlight++;
    try {
      const lesson = await generator.generate({ request, level: body.level, requestLanguage: body.language || '', startImage });
      await store.putScenario(userId, lesson);
      res.status(201).json(lesson);
    } finally { inFlight--; }
  }));
  app.get('/api/scenarios', protect, route(async (_req, res, userId) => res.json({ scenarios: await store.listScenarios(userId) })));
  app.delete('/api/scenarios/:id', protect, route(async (req, res, userId) => { await store.deleteScenario(userId, req.params.id); res.json({ ok: true }); }));
}
