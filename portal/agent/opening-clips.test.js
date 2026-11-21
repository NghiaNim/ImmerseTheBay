import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, rm, symlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import express from 'express';
import { createLessonStore } from './lessons.js';
import { registerAgentRoutes } from './routes.js';
import { openingClipKey } from './opening-clips.js';

test('opens only the clip for an exact authored scene revision and serves authorized ranges', async (t) => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'itb-openings-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  const store = createLessonStore();
  const lesson = 'ja-cafe-demo-001';
  const scene = 'cafe-order';
  const key = await openingClipKey(store, lesson, scene);
  const filename = path.join(root, `${key}.mp4`);
  const otherFilename = path.join(root, 'old-revision.mp4');
  await writeFile(otherFilename, 'fixture bytes');
  await symlink(otherFilename, filename);
  const app = express();
  registerAgentRoutes(app, { store, openingClipRoot: root, authorized: req => req.get('portal_token') === 'test' });
  const server = app.listen(0, '127.0.0.1');
  t.after(() => new Promise(resolve => server.close(resolve)));
  await new Promise(resolve => server.once('listening', resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  const request = (lessonId, sceneId, headers = { portal_token: 'test' }) =>
    fetch(`${origin}/api/agent/lessons/${lessonId}/scenes/${sceneId}/opening`, { headers });
  assert.equal((await request(lesson, scene, {})).status, 401);
  assert.equal((await request(lesson, scene)).status, 404);
  await rm(filename);
  await writeFile(filename, 'fixture bytes');
  assert.equal((await request(lesson, 'cafe-menupour')).status, 404);
  assert.equal((await request('ja-date-demo-001', scene)).status, 404);
  const response = await request(lesson, scene, { portal_token: 'test', range: 'bytes=0-6' });
  assert.equal(response.status, 206);
  assert.equal(response.headers.get('content-type'), 'video/mp4');
  assert.equal(await response.text(), 'fixture');
  const outOfRange = await request(lesson, scene, { portal_token: 'test', range: 'bytes=500-600' });
  assert.equal(outOfRange.status, 416);
  assert.equal(outOfRange.headers.get('content-range'), 'bytes */13');
  const changedStore = {
    load: async id => {
      const changed = await store.load(id);
      changed.beats[0].scene.prompt += ' The barista smiles.';
      return changed;
    },
    image: store.image,
  };
  assert.notEqual(await openingClipKey(changedStore, lesson, scene), key);
});
