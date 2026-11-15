import test from 'node:test';
import assert from 'node:assert/strict';
import { createLessonOperations } from './lesson-operations.js';
const payload = { sessionId: 'session', lessonId: 'ja-konbini-001', beat: 0, context: {} };
const response = (request, changes = {}) => ({ ok: true, json: async () => ({ visko: [], advance: 'stay', operation: { requestId: request.operation.requestId, runId: 'run-1', revision: request.operation.expectedRevision + 1, replayed: false, ...changes } }) });
const deferred = () => { let resolve; const promise = new Promise((r) => { resolve = r; }); return { promise, resolve }; };

test('lost response is retried once with byte-identical payload and request ID', async () => {
  const calls = [];
  const operations = createLessonOperations({ requestId: () => 'request-1', headers: () => ({ portal_token: 'fixture' }), fetchImpl: async (url, init) => {
    calls.push({ url, ...init });
    if (calls.length === 1) throw new TypeError('Network response lost after server commit');
    return response(JSON.parse(init.body), { replayed: true });
  } });
  const result = await operations.turn(payload);
  assert.equal(calls.length, 2); assert.equal(calls[0].body, calls[1].body);
  assert.equal(calls[0].headers.portal_token, 'fixture');
  assert.deepEqual(result.request.operation, { requestId: 'request-1', expectedRevision: 0 });
  assert.equal(result.decision.operation.replayed, true);
  assert.equal(result.claimExecution(), true); assert.equal(result.claimExecution(), false);
});

test('HTTP conflict, malformed JSON and abort are never automatically retried', async () => {
  for (const kind of ['http', 'json', 'abort']) {
    let calls = 0;
    const operations = createLessonOperations({ requestId: () => 'request', fetchImpl: async () => {
      calls++;
      if (kind === 'abort') throw new DOMException('Canceled', 'AbortError');
      if (kind === 'json') return { ok: true, json: async () => { throw new SyntaxError('Malformed response'); } };
      return { ok: false, status: 409, json: async () => ({ error: 'Stale revision' }) };
    } });
    await assert.rejects(operations.turn(payload)); assert.equal(calls, 1);
  }
  const controller = new AbortController(); controller.abort();
  const operations = createLessonOperations({ fetchImpl: () => { throw new Error('Must not fetch'); } });
  await assert.rejects(operations.turn(payload, { signal: controller.signal }), { name: 'AbortError' });
});

test('two transport failures exhaust the bounded retry and do not advance revision', async () => {
  let count = 0;
  const operations = createLessonOperations({ requestId: () => 'request', fetchImpl: async () => { count++; throw new TypeError('Network unavailable'); } });
  await assert.rejects(operations.turn(payload), /Network unavailable/); assert.equal(count, 2);
});

test('success adopts server run and revision before a new operation; commands are claimed once', async () => {
  const requests = []; let id = 0;
  const operations = createLessonOperations({ requestId: () => `request-${++id}`, fetchImpl: async (_url, init) => { const request = JSON.parse(init.body); requests.push(request); return response(request); } });
  const first = await operations.turn(payload); assert.equal(first.claimExecution(), true);
  await operations.turn(payload);
  assert.deepEqual(requests[1].operation, { requestId: 'request-2', runId: 'run-1', expectedRevision: 1 });
});

test('reset and stale epoch reject delayed responses without contaminating the new run', async () => {
  const old = deferred(); const requests = []; let id = 0;
  const operations = createLessonOperations({ requestId: () => `request-${++id}`, fetchImpl: async (_url, init) => {
    const request = JSON.parse(init.body); requests.push(request);
    return requests.length === 1 ? old.promise : response(request, { runId: 'run-2' });
  } });
  const oldTurn = operations.turn(payload); operations.reset();
  const next = await operations.turn(payload); assert.equal(next.claimExecution(), true);
  old.resolve(response(requests[0]));
  await assert.rejects(oldTurn, { name: 'AbortError' });
  await operations.turn(payload);
  assert.equal(requests[2].operation.runId, 'run-2'); assert.equal(requests[2].operation.expectedRevision, 1);
  const epochResponse = deferred(); let isCurrent = true;
  const scoped = createLessonOperations({ fetchImpl: async () => epochResponse.promise });
  const late = scoped.turn(payload, { isCurrent: () => isCurrent }); isCurrent = false;
  epochResponse.resolve(response({ operation: { requestId: 'unused', expectedRevision: 0 } }));
  await assert.rejects(late, { name: 'AbortError' });
});

test('mismatched operation metadata cannot be adopted or executed', async () => {
  for (const changes of [{ requestId: 'different' }, { revision: 9 }, { runId: '' }, { replayed: undefined }]) {
    const operations = createLessonOperations({ requestId: () => 'request', fetchImpl: async (_url, init) => response(JSON.parse(init.body), changes) });
    await assert.rejects(operations.turn(payload), /invalid operation/);
  }
});
