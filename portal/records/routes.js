import express from 'express';
import { sanitizeJpeg, decodeDataUrl, PhotoError } from './jpeg.js';
import { summarizeEvents, compileReplay } from './compile.js';
import { LOCAL_USER } from './store.js';
export const MAX_PHOTO_BYTES = 4 * 1024 * 1024;
const MAX_THUMB_BYTES = 256 * 1024;

/**
 * Photos, saved session records and replay compilation. Register BEFORE the
 * portal's global 64 KiB JSON parser (photo uploads are larger).
 * `userOf` is the single identity seam: one local learner until profiles/sign-in exist.
 */
export function registerRecordRoutes(app, { authorized = () => true, store, lessons, userOf = () => LOCAL_USER } = {}) {
  const protect = (req, res, next) => { res.set('Cache-Control', 'no-store'); return authorized(req) ? next() : res.status(401).json({ error: 'portal_token required' }); };
  const route = (fn) => async (req, res) => {
    try { await fn(req, res, userOf(req)); }
    catch (e) { res.status(e.status || 500).json({ error: e.status ? e.message : 'Session storage unavailable' }); if (!e.status) console.error('[records]', e); }
  };

  app.post('/api/photos', protect, express.json({ limit: '7mb', strict: true }), route(async (req, res, userId) => {
    const body = req.body;
    if (!body || typeof body !== 'object' || Array.isArray(body) || Object.keys(body).some((k) => !['image', 'thumb'].includes(k))) throw new PhotoError(400, 'Send {image, thumb} JPEG data URLs');
    const image = decodeDataUrl(body.image, 'image');
    const thumb = decodeDataUrl(body.thumb, 'thumb');
    if (image.length > MAX_PHOTO_BYTES) throw new PhotoError(413, 'Photo exceeds 4 MiB');
    if (thumb.length > MAX_THUMB_BYTES) throw new PhotoError(413, 'Thumbnail exceeds 256 KiB');
    const clean = sanitizeJpeg(image);
    const cleanThumb = sanitizeJpeg(thumb);
    res.status(201).json(await store.putPhoto({ userId, jpeg: clean.jpeg, thumb: cleanThumb.jpeg, width: clean.width, height: clean.height }));
  }), (error, _req, res, _next) => {
    res.set('Cache-Control', 'no-store').status(error.type === 'entity.too.large' ? 413 : 400).json({ error: error.type === 'entity.too.large' ? 'Photo upload exceeds the size limit' : 'Invalid photo upload' });
  });
  app.get('/api/photos', protect, route(async (_req, res, userId) => res.json({ photos: await store.listPhotos(userId) })));
  app.get('/api/photos/:id/:variant(image|thumb)', protect, route(async (req, res, userId) => {
    res.set('Cache-Control', 'private, max-age=3600').type('image/jpeg').sendFile(await store.photoFile(userId, req.params.id, req.params.variant));
  }));
  app.delete('/api/photos/:id', protect, route(async (req, res, userId) => { await store.deletePhoto(userId, req.params.id); res.json({ ok: true }); }));

  app.get('/api/sessions', protect, route(async (_req, res, userId) => {
    const records = await store.listRecords(userId);
    const sessions = [];
    for (const record of records) sessions.push({ ...record, stats: summarizeEvents(await store.events(userId, record.id)).stats });
    res.json({ sessions });
  }));
  app.get('/api/sessions/:id', protect, route(async (req, res, userId) => {
    const record = await store.getRecord(userId, req.params.id);
    res.json({ record, ...summarizeEvents(await store.events(userId, record.id)) });
  }));
  app.delete('/api/sessions/:id', protect, route(async (req, res, userId) => { await store.deleteRecord(userId, req.params.id); res.json({ ok: true }); }));
  app.post('/api/sessions/:id/replay', protect, express.json({ limit: '1kb' }), route(async (req, res, userId) => {
    const deck = req.body?.deck ?? 'all';
    const record = await store.getRecord(userId, req.params.id);
    const summary = summarizeEvents(await store.events(userId, record.id));
    const original = await lessons.load(record.lessonId);
    const lesson = await store.putReplay(userId, (id) => compileReplay({ id, record, summary, original, deck }));
    res.status(201).json({ lessonId: lesson.id, title: lesson.title, lineCount: lesson.beats.reduce((n, b) => n + b.lines.length, 0) });
  }));
}
