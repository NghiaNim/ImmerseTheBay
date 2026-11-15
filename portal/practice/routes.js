import express from 'express';
import { AgentError } from '../agent/lessons.js';
import { createPracticeService } from './service.js';

// Register before the global 64 KiB parser. Photos never touch disk or logs.
export function registerPracticeRoutes(app, { authorized = () => true, store, service = createPracticeService({ store }) } = {}) {
  const protect = (req, res, next) => authorized(req) ? next() : res.status(401).json({ error: 'portal_token required' });
  const errorHandler = (error, _req, res, _next) => {
    const status = error instanceof AgentError ? error.status : error.type === 'entity.too.large' ? 413 : error.type === 'entity.parse.failed' ? 400 : 500;
    res.set('Cache-Control', 'no-store').status(status).json({ error: error instanceof AgentError ? error.message : status === 413 ? 'Photo request exceeds 4.1 MiB' : status === 400 ? 'Invalid JSON request' : 'Practice service unavailable' });
  };
  app.post('/api/practice/generate', protect, express.json({ limit: '4.1mb', strict: true, inflate: false }), async (req, res, next) => {
    const controller = new AbortController();
    const close = () => { if (!res.writableEnded) controller.abort(); };
    res.on('close', close);
    try { const result = await service.generate(req.body, { caller: req.ip || 'local', signal: controller.signal }); if (!controller.signal.aborted) res.set('Cache-Control', 'no-store').json(result); }
    catch (error) { if (!controller.signal.aborted) next(error); }
    finally { res.off('close', close); }
  }, errorHandler);
  app.get('/api/practice/images/:id', protect, (req, res, next) => {
    try { const image = store.ephemeralImage(req.params.id); res.set('Cache-Control', 'no-store').type(image.mimeType).send(image.bytes); }
    catch (error) { next(error); }
  }, errorHandler);
}
