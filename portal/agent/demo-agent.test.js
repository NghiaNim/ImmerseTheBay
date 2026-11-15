import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createAgentService, validateRequest } from './service.js';
const lesson = JSON.parse(await readFile(new URL('./fixtures/ja-konbini-001.json', import.meta.url)));
function setup(options = {}) {
  const logs = []; let calls = 0;
  const service = createAgentService({ store: { load: async () => lesson }, llm: async ({ fallback }) => { calls++; return fallback; }, planner: null,
    promptIntervalMs: 0, log: async (entry) => logs.push(entry), ...options });
  let previous, serial = 0;
  const body = (changes = {}) => ({ sessionId: 'demo-agent', lessonId: lesson.id, beat: previous?.route.returnBeat || 0, context: {}, ...changes,
    operation: { requestId: `request-${++serial}`, expectedRevision: previous?.operation.revision || 0, ...(previous ? { runId: previous.operation.runId } : {}) } });
  const turn = async (changes) => { const request = body(changes); const response = await service.turn(request); previous = response; return { request, response }; };
  return { service, turn, body, logs, calls: () => calls };
}
test('new goal replaces future detour nodes and preserves checkpoint with replay safety', async () => {
  const { service, turn, logs } = setup();
  const opening = (await turn()).response;
  const first = (await turn({ intent: 'I want a sandwich' })).response;
  await turn({ context: { gesture: 'pinch' } });
  const { request, response } = await turn({ intent: 'Where is the checkout counter?' });
  assert.equal(response.route.kind, 'detour'); assert.equal(response.route.step, 1); assert.equal(response.route.total, 2);
  assert.equal(response.route.returnBeat, opening.route.returnBeat);
  assert.notEqual(response.route.nodeId, first.route.nodeId); assert.notEqual(response.overlay.jp, first.overlay.jp);
  assert.equal(response.advance, 'stay'); assert.ok(response.visko.every((command) => command.name === 'set_prompt'));
  const count = logs.length;
  assert.equal((await service.turn(request)).operation.replayed, true); assert.equal(logs.length, count);
  const graph = logs.at(-1).graph; const order = new Map(graph.nodes.map((node, i) => [node.nodeId, i]));
  assert.ok(graph.edges.every(({ from, to }) => order.get(from) < order.get(to)));
});
test('invalid replacement and identical intent preserve the current branch; successful replacement retires its memory', async () => {
  let captured;
  const { turn } = setup({ planner: async (input) => { captured = input; throw new Error('provider unavailable'); } });
  await turn(); const first = (await turn({ intent: 'I want a sandwich' })).response;
  const repeated = (await turn({ intent: 'I want a sandwich' })).response;
  assert.equal(repeated.route.nodeId, first.route.nodeId);
  const failed = (await turn({ intent: 'Please heat this bento' })).response;
  assert.equal(captured.returnBeat, first.route.returnBeat);
  assert.equal(failed.route.nodeId, first.route.nodeId); assert.equal(failed.overlay.jp, first.overlay.jp);
  assert.match(failed.feedback.notes.join(' '), /unavailable/);
  const replacement = (await turn({ intent: 'Where is the checkout counter?' })).response;
  assert.notEqual(replacement.route.nodeId, first.route.nodeId);
  await turn({ action: 'return_to_main' });
  await turn({ intent: 'Please heat this bento' });
  assert.ok(captured.sceneContext.recentActions.every((action) => !action.action.includes('sandwich')));
});
test('explicit return appends one forward rejoin, retires branch memory and never resets video', async () => {
  const { service, turn, logs } = setup();
  const opening = (await turn()).response;
  await turn({ intent: 'I want a sandwich' });
  const { request, response } = await turn({ action: 'return_to_main' });
  assert.equal(response.route.kind, 'rejoin'); assert.equal(response.overlay.jp, opening.overlay.jp);
  assert.equal(response.advance, 'stay'); assert.ok(response.visko.every((command) => command.name === 'set_prompt'));
  assert.ok(response.speech.text.includes('戻り'));
  const node = response.route.nodeId;
  assert.equal((await service.turn(request)).route.nodeId, node);
  const again = (await turn({ action: 'return_to_main' })).response;
  assert.equal(again.route.nodeId, node); assert.deepEqual(again.visko, []); assert.equal(again.speech, null);
  assert.equal(logs.at(-1).graph.nodes.filter((entry) => entry.kind === 'rejoin').length, 1);
});
test('idle invitation is once per node, with no model calls, scoring, progress or video commands', async () => {
  const { service, turn, logs, calls } = setup();
  const first = (await turn()).response; const initialCalls = calls(); const initialGraph = logs.at(-1).graph;
  const { request, response } = await turn({ action: 'idle_hint' });
  assert.deepEqual(response.overlay, first.overlay); assert.deepEqual(response.route, first.route);
  assert.deepEqual(response.visko, []); assert.equal(response.advance, 'stay'); assert.equal(response.feedback, null);
  assert.equal(response.speech.role, 'clerk'); assert.ok(response.speech.text.includes('か'));
  assert.equal(calls(), initialCalls); assert.deepEqual(logs.at(-1).graph, initialGraph);
  assert.deepEqual((await service.turn(request)).speech, response.speech);
  const repeated = (await turn({ action: 'idle_hint' })).response;
  assert.equal(repeated.speech, null); assert.deepEqual(repeated.visko, []); assert.equal(calls(), initialCalls);
  const branch = (await turn({ intent: 'I want a sandwich' })).response;
  const branchInvite = (await turn({ action: 'idle_hint' })).response;
  assert.equal(branchInvite.route.nodeId, branch.route.nodeId); assert.equal(branchInvite.route.step, 1);
  assert.deepEqual(branchInvite.overlay, branch.overlay); assert.ok(branchInvite.speech.text.includes('戻り'));
});
test('replacement budget counts valid branches only, and return remains available at the limit', async () => {
  const { turn } = setup(); await turn();
  let previous;
  for (let i = 0; i < 6; i++) previous = (await turn({ intent: i % 2 ? 'Where is the checkout counter?' : 'I want a sandwich' })).response;
  const limited = (await turn({ intent: 'Find a product on the shelf' })).response;
  assert.equal(limited.route.nodeId, previous.route.nodeId); assert.equal(limited.overlay.jp, previous.overlay.jp);
  assert.match(limited.feedback.notes.join(' '), /Six side/);
  assert.equal((await turn({ action: 'return_to_main' })).response.route.kind, 'rejoin');
});
test('control actions reject before start, after completion and when mixed with user input', async () => {
  const { service, turn, body } = setup();
  await assert.rejects(service.turn(body({ action: 'idle_hint' })), { status: 409 });
  for (const changes of [{ action: 'unknown' }, { action: 'idle_hint', intent: 'sandwich' }, { action: 'return_to_main', transcript: 'speech' }, { action: 'idle_hint', context: { gesture: 'pinch' } }]) {
    assert.throws(() => validateRequest(body(changes)), { status: 400 });
  }
  let result = (await turn()).response;
  while (result.advance !== 'scene_done') result = (await turn({ context: { gesture: 'pinch' } })).response;
  await assert.rejects(service.turn(body({ action: 'idle_hint' })), { status: 409 });
});
