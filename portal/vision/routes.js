import express from 'express';
import { createVisionService } from './service.js';
import { VisionError } from './schema.js';

/** Register BEFORE the portal's smaller global express.json() parser. */
export function registerVisionRoutes(app, { authorized = () => true, service = createVisionService() } = {}) {
  const protect = (req, res, next) => authorized(req) ? next() : res.status(401).json({ error: 'portal_token required' });
  app.post('/api/vision/point', protect, express.json({ limit: '3mb', strict: true }), async (req, res, next) => {
    try {
      res.set('Cache-Control', 'no-store');
      res.json(await service.point(req.body, { caller: req.ip || 'local' }));
    } catch (error) { next(error); }
  }, (error, _req, res, _next) => {
    const status = error instanceof VisionError ? error.status : error.type === 'entity.too.large' ? 413 : error.type === 'entity.parse.failed' ? 400 : 500;
    res.set('Cache-Control', 'no-store').status(status).json({ error: error instanceof VisionError ? error.message : status === 413 ? 'Frame request exceeds 3 MiB' : status === 400 ? 'Invalid JSON request' : 'Vision service unavailable' });
  });
}
