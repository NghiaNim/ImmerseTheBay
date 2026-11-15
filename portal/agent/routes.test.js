import test from 'node:test';
import assert from 'node:assert/strict';
import express from 'express';
import { registerAgentRoutes } from './routes.js';
import { createLessonStore } from './lessons.js';
import { createAgentService } from './service.js';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
test('HTTP catalog/turn/reset honor authorization and never serve private files', async () => {
  const root = await mkdtemp(path.join(os.tmpdir(), 'il-http-'));
  const line = { jp: 'どうぞ。', kana: 'どうぞ。', romaji: 'Dozo.', zh: '请。', en: 'Please.', cue: 'The host points at a cushion.' };
  const lesson = { id: 'ja-demo', title: 'Home', level: 'N1', scenePrompt: 'A host sits in a tatami room.', audioPrompt: 'Rain taps the roof.', startImage: 'lessons/images/home.jpg', beats: [{ goal: 'Welcome', lines: [line], vocab: [] }] };
  await mkdir(path.join(root, 'private')); await mkdir(path.join(root, 'images')); await writeFile(path.join(root, 'ja-demo.json'), JSON.stringify(lesson)); await writeFile(path.join(root, 'private', 'secret.json'), 'private text'); await writeFile(path.join(root, 'images', 'home.jpg'), 'jpeg fixture');
  const store = createLessonStore(root); const service = createAgentService({ store, llm: null, log: async () => {}, promptIntervalMs: 0 });
  const app = express(); app.use(express.json()); registerAgentRoutes(app, { store, service, authorized: (req) => req.get('portal_token') === 'fixture-token' });
  const server = app.listen(0, '127.0.0.1'); await new Promise((resolve) => server.once('listening', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const get = (url) => fetch(base + url, { headers: { portal_token: 'fixture-token' } });
  const post = (url, body) => fetch(base + url, { method: 'POST', headers: { portal_token: 'fixture-token', 'content-type': 'application/json' }, body: JSON.stringify(body) });
  try {
    assert.equal((await fetch(base + '/api/agent/lessons')).status, 401);
    assert.equal((await (await get('/api/agent/lessons')).json()).lessons[0].lineCount, 1);
    assert.equal((await (await get('/api/agent/lessons/ja-demo')).json()).id, 'ja-demo');
    assert.equal((await get('/lessons/images/home.jpg')).status, 200);
    assert.equal((await get('/lessons/private/secret.json')).status, 404);
    assert.equal((await get('/lessons/images/SOURCES.md')).status, 404);
    assert.equal((await get('/api/agent/lessons/unknown')).status, 404);
    const body = { sessionId: 'fixture-session', lessonId: 'ja-demo', beat: 0, context: {} };
    const r = await (await post('/api/agent/turn', body)).json(); assert.equal(r.visko.at(-1).name, 'start');
    assert.equal((await post('/api/agent/turn', { ...body, context: { rms: 'bad' } })).status, 400);
    assert.deepEqual(await (await post('/api/agent/reset', { sessionId: body.sessionId, lessonId: body.lessonId })).json(), { ok: true });
  } finally { await new Promise((resolve) => server.close(resolve)); await rm(root, { recursive: true, force: true }); }
});

test('clerk audio reads text and style from the exact committed run and revision', async (t) => {
  const app = express(); app.use(express.json());
  const ids = { sessionId: 'session-1', lessonId: 'lesson-1' };
  const spoken = [];
  const service = { snapshot: (input) => {
    assert.deepEqual(input, ids);
    return { runId: 'run-1', revision: 2,
      response: { speech: { role: 'clerk', text: 'いらっしゃいませ。', language: 'ja' } },
      lessonContext: { partnerVoice: 'male' } };
  } };
  registerAgentRoutes(app, { service, synthesize: async ({ signal, ...voice }) => {
    assert.equal(signal.aborted, false);
    spoken.push(voice);
    return Buffer.from('test audio');
  } });
  const server = app.listen(0, '127.0.0.1');
  await new Promise((resolve) => server.once('listening', resolve));
  t.after(() => new Promise((resolve) => server.close(resolve)));
  const request = (body) => fetch(`http://127.0.0.1:${server.address().port}/api/agent/clerk-speech`, {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body),
  });
  const exact = { ...ids, runId: 'run-1', revision: 2 };
  assert.equal((await request({ ...exact, partnerVoice: 'female' })).status, 400);
  assert.equal((await request({ ...exact, runId: 'another-run' })).status, 409);
  assert.equal((await request({ ...exact, revision: 1 })).status, 409);
  const response = await request(exact);
  assert.equal(response.status, 200);
  assert.match(response.headers.get('content-type'), /audio\/wav/);
  assert.deepEqual(spoken, [{ text: 'いらっしゃいませ。', language: 'ja', partnerVoice: 'male' }]);
});
