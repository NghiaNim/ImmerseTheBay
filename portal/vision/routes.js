import express from 'express';
import { createVisionService } from './service.js';
import { VisionError } from './schema.js';
import { createExplorationService } from './explore.js';

/** Register BEFORE the portal's smaller global express.json() parser. */
export function registerVisionRoutes(app, {
  authorized = () => true, service = createVisionService(), snapshot,
  exploration = snapshot ? createExplorationService({ snapshot, vision: service }) : null,
} = {}) {
  const protect = (req, res, next) => authorized(req) ? next() : res.status(401).json({ error: 'portal_token required' });
  const frameParser = express.json({ limit: '3mb', strict: true });
  const fail = (error, _req, res, _next) => {
    const status = error instanceof VisionError ? error.status : error.type === 'entity.too.large' ? 413 : error.type === 'entity.parse.failed' ? 400 : 500;
    res.set('Cache-Control', 'no-store').status(status).json({ error: error instanceof VisionError ? error.message : status === 413 ? 'Frame request exceeds 3 MiB' : status === 400 ? 'Invalid JSON request' : 'Vision service unavailable' });
  };
  app.post('/api/vision/point', protect, frameParser, async (req, res, next) => {
    try {
      res.set('Cache-Control', 'no-store');
      res.json(await service.point(req.body, { caller: req.ip || 'local' }));
    } catch (error) { next(error); }
  }, fail);
  if (exploration) app.post('/api/vision/explore', protect, frameParser, async (req, res, next) => {
    const controller = new AbortController();
    const abort = () => { if (!res.writableEnded) controller.abort(); };
    res.on('close', abort);
    try {
      const reply = await exploration.explore(req.body, { caller: req.ip || 'local', signal: controller.signal });
      if (!controller.signal.aborted) res.set('Cache-Control', 'no-store').json(reply);
    } catch (error) {
      if (!controller.signal.aborted) next(error);
    } finally { res.off('close', abort); }
  }, fail);
}
