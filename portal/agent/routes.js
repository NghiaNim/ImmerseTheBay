import express from 'express';
import { createCartesiaSpeech, stereoSpeechWav } from './cartesia.js';
import { createTranscriber, audioType, MAX_AUDIO_BYTES } from './transcribe.js';
import { AgentError, createLessonStore } from './lessons.js';
import { createDialogueTranslator } from './translate.js';
import { createAgentService } from './service.js';
import { OPENING_CLIP_ROOT, resolveOpeningClip } from './opening-clips.js';
export function registerAgentRoutes(app, { authorized = () => true, store = createLessonStore(), service = createAgentService({ store }), transcribe = createTranscriber(), synthesize = createCartesiaSpeech(), translate = createDialogueTranslator(), openingClipRoot = OPENING_CLIP_ROOT } = {}) {
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
  app.post('/api/agent/translate', protect, route(async (req, res) => {
    const text = typeof req.body?.text === 'string' ? req.body.text.trim() : '';
    const { includeRomaji } = req.body || {};
    if (!text || text.length > 2000 || (includeRomaji !== undefined && typeof includeRomaji !== 'boolean'))
      return res.status(400).json({ error: 'Invalid utterance' });
    const controller = new AbortController();
    const abort = () => { if (!res.writableEnded) controller.abort(); };
    res.on('close', abort);
    try {
      const caption = await translate({ text, ...(includeRomaji ? { includeRomaji } : {}), signal: controller.signal });
      if (!controller.signal.aborted) res.set('Cache-Control', 'no-store').json(includeRomaji ? caption : { english: caption });
    } finally { res.off('close', abort); }
  }));
  async function sendSpeech(req, res, params) {
    const layout = req.get('x-tenin-audio-layout');
    if (layout !== undefined && layout !== 'stereo') throw new AgentError(400, 'Unsupported speech audio layout');
    const controller = new AbortController();
    const abort = () => { if (!res.writableEnded) controller.abort(); };
    res.on('close', abort);
    try {
      const audio = await synthesize({ ...params, signal: controller.signal });
      if (!controller.signal.aborted)
        res.set('Cache-Control', 'no-store').type('audio/wav').send(layout === 'stereo' ? stereoSpeechWav(audio) : audio);
    } finally { res.off('close', abort); }
  }
  app.post('/api/agent/speak', protect, route(async (req, res) => {
    if (!req.body || typeof req.body !== 'object' || Array.isArray(req.body) || Object.keys(req.body).some((key) => !['text', 'language'].includes(key))) return res.status(400).json({ error: 'Invalid speech request' });
    await sendSpeech(req, res, req.body);
  }));
  app.post('/api/agent/clerk-speech', protect, route(async (req, res) => {
    const input = req.body;
    if (!input || typeof input !== 'object' || Array.isArray(input)
        || Object.keys(input).some((key) => !['sessionId', 'lessonId', 'runId', 'revision'].includes(key))
        || typeof input.runId !== 'string' || !Number.isSafeInteger(input.revision) || input.revision < 1)
      return res.status(400).json({ error: 'Invalid clerk speech request' });
    const state = service.snapshot({ sessionId: input.sessionId, lessonId: input.lessonId });
    if (!state || state.runId !== input.runId || state.revision !== input.revision)
      return res.status(409).json({ error: 'Lesson changed before clerk playback' });
    const speech = state.response?.speech;
    if (speech?.role !== 'clerk' || !speech.text)
      return res.status(404).json({ error: 'There is no clerk line for this turn' });
    await sendSpeech(req, res, { text: speech.text, language: speech.language, partnerVoice: state.lessonContext?.partnerVoice });
  }));
  app.get('/api/agent/lessons', protect, route(async (_req, res) => res.json({ lessons: await store.list() })));
  app.get('/api/agent/lessons/:id', protect, route(async (req, res) => res.json(await store.load(req.params.id))));
  app.get('/api/agent/lessons/:id/scenes/:sceneId/opening', protect, route(async (req, res) => {
    const clip = await resolveOpeningClip(store, openingClipRoot, req.params.id, req.params.sceneId);
    if (!clip) return res.set('Cache-Control', 'no-store').status(404).json({ error: 'Scene opening unavailable' });
    res.sendFile(clip, { cacheControl: false, headers: { 'Cache-Control': 'private, no-cache' } }, error => {
      if (!error) return;
      if (res.headersSent) return res.destroy(error);
      res.set('Cache-Control', 'no-store').status(error.status === 404 ? 404 : 500)
        .json({ error: 'Scene opening unavailable' });
    });
  }));
  app.get('/lessons/images/:filename', protect, route(async (req, res) => res.sendFile(await store.image(req.params.filename))));
  app.post('/api/agent/summary', protect, route(async (req, res) => res.set('Cache-Control', 'no-store').json(service.summary(req.body))));
  app.post('/api/agent/snapshot', protect, route(async (req, res) => res.set('Cache-Control', 'no-store').json(service.snapshot(req.body))));
  app.post('/api/agent/reset', protect, route(async (req, res) => res.json(service.reset(req.body))));
  app.post('/api/agent/turn', protect, route(async (req, res) => res.json(await service.turn(req.body))));
}
