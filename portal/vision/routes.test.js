import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { registerVisionRoutes } from './routes.js';
import { createVisionService } from './service.js';
import { noHit } from './schema.js';

test('HTTP authorizes before image parsing, accepts >64KiB frames and keeps other routes small', async () => {
  let calls = 0;
  const app = express();
  registerVisionRoutes(app, { authorized: (req) => req.get('portal_token') === 'test-token',
    service: { point: async (body, options) => { calls++; assert(body.frame.length > 65536); assert(options.caller); return noHit(); } } });
  app.use(express.json({ limit: '64kb' }));
  app.post('/api/small', (_req, res) => res.json({ ok: true }));
  app.use((error, _req, res, _next) => res.status(error.status || 500).json({ error: 'small route error' }));
  const server = app.listen(0, '127.0.0.1');
  await new Promise((resolve) => server.once('listening', resolve));
  const url = `http://127.0.0.1:${server.address().port}`;
  const post = (path, body, auth = true) => fetch(url + path, { method: 'POST', headers: { 'content-type': 'application/json', ...(auth ? { portal_token: 'test-token' } : {}) }, body });
  try {
    const malformed = await post('/api/vision/point', '{', false);
    assert.equal(malformed.status, 401); await malformed.text(); assert.equal(calls, 0);
    const invalid = await post('/api/vision/point', '{');
    assert.equal(invalid.status, 400); assert.equal(invalid.headers.get('cache-control'), 'no-store'); await invalid.text();
    const body = JSON.stringify({ frame: 'x'.repeat(70_000), point: { x: 0.5, y: 0.5 }, gesture: 'point' });
    const response = await post('/api/vision/point', body);
    assert.equal(response.status, 200); assert.equal(response.headers.get('cache-control'), 'no-store');
    assert.deepEqual(await response.json(), noHit()); assert.equal(calls, 1);
    const small = await post('/api/small', body); assert.equal(small.status, 413); await small.text();
    const oversized = await post('/api/vision/point', JSON.stringify({ frame: 'x'.repeat(3 * 1024 * 1024) }));
    assert.equal(oversized.status, 413); assert.deepEqual(await oversized.json(), { error: 'Frame request exceeds 3 MiB' });
    assert.equal(calls, 1);
  } finally { server.closeAllConnections(); await new Promise((resolve) => server.close(resolve)); }
});

test('HTTP validates before model invocation and redacts unexpected internal errors', async () => {
  let calls = 0;
  const app = express();
  registerVisionRoutes(app, { service: createVisionService({ client: async () => { calls++; return {}; } }) });
  const server = app.listen(0, '127.0.0.1');
  await new Promise((resolve) => server.once('listening', resolve));
  try {
    const response = await fetch(`http://127.0.0.1:${server.address().port}/api/vision/point`, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ frame: 'not an image', point: { x: 0.5, y: 0.5 }, gesture: 'point' }),
    });
    assert.equal(response.status, 400); assert.equal(calls, 0); assert.doesNotMatch(JSON.stringify(await response.json()), /not an image/);
  } finally { server.closeAllConnections(); await new Promise((resolve) => server.close(resolve)); }
});
