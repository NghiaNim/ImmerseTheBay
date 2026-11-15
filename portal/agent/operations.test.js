import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import express from 'express';
import { createAgentService, validateRequest } from './service.js';
import { registerAgentRoutes } from './routes.js';
import { WIRE_RESPONSE_SCHEMA, validateSchema } from './schema.js';
import { createOperationCache, rememberOperation, replayOperation } from './operations.js';

const lesson = JSON.parse(await readFile(new URL('./fixtures/ja-konbini-001.json', import.meta.url)));
const request = (id, previous, changes = {}) => ({ sessionId: 'replay-session', lessonId: lesson.id, beat: previous?.route.returnBeat || 0,
  context: {}, ...changes, operation: { requestId: id, expectedRevision: previous?.operation.revision || 0, ...(previous ? { runId: previous.operation.runId } : {}) } });
function setup(options = {}) {
  const logs = [];
  const service = createAgentService({ store: { load: async () => lesson }, llm: null, planner: null, promptIntervalMs: 0, log: async (entry) => logs.push(entry), ...options });
  return { service, logs };
}
test('dropped detour response replays before cursor checks without advancing or logging twice', async () => {
  const { service, logs } = setup();
  const opening = await service.turn(request('open'));
  const branch = await service.turn(request('branch', opening, { intent: 'I want a sandwich' }));
  const next = request('next', branch, { context: { gesture: 'pinch' } });
  const lost = await service.turn(next);
  const replay = await service.turn(next);
  assert.equal(lost.route.step, 2); assert.equal(replay.route.step, 2);
  assert.deepEqual(replay, { ...lost, operation: { ...lost.operation, replayed: true } });
  assert.equal(logs.length, 3); assert.ok(validateSchema(replay, WIRE_RESPONSE_SCHEMA));
  assert.equal(service.snapshot({ sessionId: next.sessionId, lessonId: lesson.id }).revision, 3);
});
test('concurrent duplicates execute the model once and canonical object-key order replays', async () => {
  let modelCalls = 0;
  const { service } = setup({ llm: async ({ fallback }) => { modelCalls++; return fallback; } });
  const body = request('same');
  const [one, two] = await Promise.all([service.turn(body), service.turn({ ...body, operation: { expectedRevision: 0, requestId: 'same' } })]);
  assert.equal(modelCalls, 1); assert.equal(one.operation.revision, 1); assert.equal(two.operation.replayed, true);
  await assert.rejects(service.turn({ ...body, intent: 'Different goal' }), { status: 409 });
  assert.equal(modelCalls, 1);
});
test('latest lost main advance replays despite old beat, but an older cached decision cannot rewind commands', async () => {
  const { service, logs } = setup();
  const openingRequest = request('open');
  const first = await service.turn(openingRequest);
  const advanceRequest = request('advance', first, { context: { gesture: 'pinch' } });
  const advanced = await service.turn(advanceRequest);
  assert.equal(advanced.route.returnBeat, 1);
  assert.equal((await service.turn(advanceRequest)).operation.replayed, true);
  await assert.rejects(service.turn(openingRequest), { status: 409 });
  await service.turn(request('hint', advanced, { context: { gesture: 'long_pinch' } }));
  await assert.rejects(service.turn(advanceRequest), { status: 409 });
  assert.equal(logs.length, 3);
});
test('fresh stale revisions and wrong runs reject; legacy successful turns invalidate stale clients', async () => {
  const { service } = setup(); const first = await service.turn(request('open'));
  await assert.rejects(service.turn(request('bootstrap-again')), { status: 409 });
  await assert.rejects(service.turn({ ...request('wrong-run', first), operation: { requestId: 'wrong-run', runId: 'different', expectedRevision: 1 } }), { status: 409 });
  const legacy = { sessionId: 'replay-session', lessonId: lesson.id, beat: 0, context: { gesture: 'long_pinch' } };
  await service.turn(legacy);
  await assert.rejects(service.turn(request('stale', first)), { status: 409 });
  const snapshot = service.snapshot({ sessionId: legacy.sessionId, lessonId: lesson.id });
  assert.equal(snapshot.revision, 2); assert.equal(snapshot.response.operation, undefined);
});
test('reset retires cached bootstrap IDs and snapshots are cloned and absent after reset', async () => {
  const { service } = setup(); const initial = request('open'); const first = await service.turn(initial);
  const key = { sessionId: initial.sessionId, lessonId: lesson.id };
  const snapshot = service.snapshot(key); snapshot.response.overlay.jp = 'mutated';
  assert.notEqual(service.snapshot(key).response.overlay.jp, 'mutated');
  service.reset(key); assert.equal(service.snapshot(key), null);
  await assert.rejects(service.turn(initial), { status: 409 });
  await assert.rejects(service.turn(request('old-run', first)), { status: 409 });
  const fresh = await service.turn(request('fresh'));
  assert.notEqual(fresh.operation.runId, first.operation.runId); assert.equal(fresh.operation.revision, 1);
});
test('failed logging commits neither revision nor replay entry and retry can succeed', async () => {
  let attempts = 0;
  const { service } = setup({ log: async () => { if (++attempts === 1) throw new Error('disk unavailable'); } });
  const body = request('retry'); await assert.rejects(service.turn(body), /disk unavailable/);
  assert.equal(service.snapshot({ sessionId: body.sessionId, lessonId: lesson.id }), null);
  const success = await service.turn(body);
  assert.equal(success.operation.revision, 1); assert.equal(success.operation.replayed, false);
  assert.equal((await service.turn(body)).operation.replayed, true);
});
test('operation admission and bounded replay eviction fail closed', () => {
  for (const operation of [null, {}, { requestId: '', expectedRevision: 0 }, { requestId: 'x', expectedRevision: -1 }, { requestId: 'x', expectedRevision: 0, runId: '' }, { requestId: 'x', expectedRevision: 0, unknown: true }]) {
    assert.throws(() => validateRequest({ ...request('x'), operation }), { status: 400 });
  }
  const cache = createOperationCache();
  for (let i = 0; i < 400; i++) {
    const body = request(`op-${i}`);
    rememberOperation(cache, body, { operation: { requestId: `op-${i}`, runId: 'run', revision: i + 1, replayed: false } });
  }
  assert.equal(cache.responses.size, 64); assert.equal(cache.retired.size, 256);
  assert.throws(() => replayOperation(cache, request('op-335')), { status: 409 });
});
test('snapshot HTTP route requires authorization and returns no-store server state', async (t) => {
  const { service } = setup(); const app = express(); app.use(express.json());
  registerAgentRoutes(app, { service, authorized: (req) => req.get('portal_token') === 'test' });
  const server = app.listen(0); await new Promise((resolve) => server.once('listening', resolve));
  t.after(() => { server.closeAllConnections(); server.close(); });
  const url = `http://127.0.0.1:${server.address().port}/api/agent/snapshot`;
  const options = { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sessionId: 'replay-session', lessonId: lesson.id }) };
  assert.equal((await fetch(url, options)).status, 401);
  const missing = await fetch(url, { ...options, headers: { ...options.headers, portal_token: 'test' } });
  assert.equal(await missing.json(), null); assert.equal(missing.headers.get('cache-control'), 'no-store');
  await service.turn(request('open'));
  const found = await fetch(url, { ...options, headers: { ...options.headers, portal_token: 'test' } });
  assert.equal((await found.json()).revision, 1);
});
