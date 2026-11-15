import express from 'express';
import { createCartesiaSpeech } from './cartesia.js';
import { createTranscriber, audioType, MAX_AUDIO_BYTES } from './transcribe.js';
import { createLessonStore } from './lessons.js';
import { createAgentService } from './service.js';
export function registerAgentRoutes(app, { authorized = () => true, store = createLessonStore(), service = createAgentService({ store }), transcribe = createTranscriber(), synthesize = createCartesiaSpeech() } = {}) {
  const protect = (req, res, next) => authorized(req) ? next() : res.status(401).json({ error: 'portal_token required' });
  const route = (fn) => async (req, res) => {
    try { await fn(req, res); } catch (e) { res.status(e.status || 500).json({ error: e.status ? e.message : 'Learning service unavailable' }); }
  };
  app.post('/api/agent/transcribe', protect,
    (req, res, next) => {
      try { audioType(req.get('content-type')); next(); }
      catch (error) { res.status(error.status).json({ error: error.message }); }
    },
    express.raw({ type: () => true, limit: MAX_AUDIO_BYTES, inflate: false }),
    (error, _req, res, _next) => res.status(error.status === 413 ? 413 : 400).json({
      error: error.status === 413 ? 'Audio recording exceeds 10 MiB' : 'Invalid audio upload',
    }),
    route(async (req, res) => res.json(await transcribe({
      audio: req.body, mimeType: req.get('content-type'), language: req.get('x-audio-language'),
    }))));
  app.post('/api/agent/speak', protect, route(async (req, res) => {
    if (!req.body || typeof req.body !== 'object' || Array.isArray(req.body) || Object.keys(req.body).some((key) => !['text', 'language'].includes(key))) return res.status(400).json({ error: 'Invalid speech request' });
    const controller = new AbortController();
    const abort = () => { if (!res.writableEnded) controller.abort(); };
    res.on('close', abort);
    try {
      const audio = await synthesize({ ...req.body, signal: controller.signal });
      if (!controller.signal.aborted) res.set('Cache-Control', 'no-store').type('audio/wav').send(audio);
    } finally { res.off('close', abort); }
  }));
  app.get('/api/agent/lessons', protect, route(async (_req, res) => res.json({ lessons: await store.list() })));
  app.get('/api/agent/lessons/:id', protect, route(async (req, res) => res.json(await store.load(req.params.id))));
  app.get('/lessons/images/:filename', protect, route(async (req, res) => res.sendFile(await store.image(req.params.filename))));
  app.post('/api/agent/summary', protect, route(async (req, res) => res.set('Cache-Control', 'no-store').json(service.summary(req.body))));
  app.post('/api/agent/snapshot', protect, route(async (req, res) => res.set('Cache-Control', 'no-store').json(service.snapshot(req.body))));
  app.post('/api/agent/reset', protect, route(async (req, res) => res.json(service.reset(req.body))));
  app.post('/api/agent/turn', protect, route(async (req, res) => res.json(await service.turn(req.body))));
}
