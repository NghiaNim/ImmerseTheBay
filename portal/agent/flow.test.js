import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createAgentService } from './service.js';
import { flattenLesson } from './lessons.js';
import { createClaudePlanner } from './llm.js';
const lesson = JSON.parse(await readFile(new URL('./fixtures/ja-konbini-001.json', import.meta.url), 'utf8'));
const main = flattenLesson(lesson);
function setup(options = {}) {
  const logs = [];
  const service = createAgentService({ store: { load: async () => lesson }, llm: null, planner: null, promptIntervalMs: 0, log: async (e) => logs.push(e), ...options });
  const turn = (extras = {}) => service.turn({ sessionId: 'shopping', lessonId: lesson.id, beat: 6, context: {}, ...extras });
  return { service, turn, logs };
}
test('sandwich detour has three scored targets, forward DAG visits and rejoins saved checkout', async () => {
  const { turn, logs } = setup();
  const opening = await turn(); assert.equal(opening.overlay.jp, main[6].line.jp);
  let r = await turn({ intent: '我想拿货架上的三明治' });
  assert.equal(r.route.kind, 'detour'); assert.equal(r.route.total, 3); assert.equal(r.route.step, 1); assert.equal(r.route.returnBeat, 6); assert.match(r.overlay.jp, /サンドイッチ/);
  const nodes = new Set([opening.route.nodeId, r.route.nodeId]);
  for (let step = 1; step <= 3; step++) {
    const before = r.overlay;
    r = await turn({ transcript: before.kana });
    assert.equal(r.feedback.score, 100); assert.equal(r.advance, 'stay'); assert.equal(r.route.returnBeat, 6);
    assert.ok(r.visko.every((v) => v.name === 'set_prompt')); assert.ok(!nodes.has(r.route.nodeId)); nodes.add(r.route.nodeId);
    if (step < 3) { assert.equal(r.route.step, step + 1); assert.notEqual(r.overlay.jp, before.jp); }
  }
  assert.equal(r.route.kind, 'rejoin'); assert.equal(r.overlay.jp, main[6].line.jp);
  r = await turn({ transcript: main[6].line.kana }); assert.equal(r.advance, 'next_line'); assert.equal(r.overlay.jp, main[7].line.jp);
  const graph = logs.at(-1).graph;
  assert.equal(new Set(graph.nodes.map((n) => n.nodeId)).size, graph.nodes.length);
  const order = new Map(graph.nodes.map((node, i) => [node.nodeId, i]));
  assert.ok(graph.edges.every((e) => order.get(e.from) < order.get(e.to)));
});
test('natural Chinese/Japanese/English desire starts a detour but errors and keyword mentions do not', async () => {
  for (const transcript of ['我想买一个三明治', '棚のサンドイッチが欲しい', 'I want a sandwich from the shelf']) {
    const { turn } = setup(); await turn(); const r = await turn({ transcript }); assert.equal(r.route.kind, 'detour', transcript);
  }
  for (const transcript of ['サンドイッチ', 'すみません', 'れじぶくろおねがい']) {
    const { turn } = setup(); await turn(); const r = await turn({ transcript }); assert.equal(r.route.kind, 'main', transcript);
  }
});
test('detour hints, wrong speech, repeated intent stay and cannot alter saved checkpoint', async () => {
  const { turn } = setup(); await turn(); let r = await turn({ intent: 'I want a sandwich' }); const jp = r.overlay.jp; const id = r.route.nodeId;
  r = await turn({ transcript: '違います' }); assert.equal(r.overlay.jp, jp); assert.equal(r.route.nodeId, id); assert.ok(r.feedback.score < 75);
  r = await turn({ context: { gesture: 'long_pinch' } }); assert.equal(r.overlay.jp, jp); assert.ok(r.overlay.hintLevel > 0);
  r = await turn({ intent: 'I want a sandwich' }); assert.equal(r.overlay.jp, jp); assert.equal(r.route.nodeId, id);
  await assert.rejects(turn({ beat: 7, transcript: jp }), { status: 409 });
});
test('counter and generic product work with no key; branches are bounded to six per run', async () => {
  const { turn } = setup(); await turn();
  for (let i = 0; i < 6; i++) {
    let r = await turn({ intent: i % 2 ? 'Find a product on the shelf' : 'I want the checkout counter' }); assert.equal(r.route.kind, 'detour');
    while (r.route.kind === 'detour') r = await turn({ transcript: r.overlay.kana });
    assert.equal(r.route.kind, 'rejoin'); assert.equal(r.overlay.jp, main[6].line.jp);
  }
  const r = await turn({ intent: 'I want a sandwich' }); assert.notEqual(r.route.kind, 'detour'); assert.match(r.feedback.notes.join(' '), /Six side/);
});
const generated = {
  label: 'Ask for warm tea', returnBeat: 6,
  nodes: [{ jp: '温かいお茶はありますか。', kana: 'あたたかいおちゃはありますか。', romaji: 'Atatakai ocha wa arimasu ka.', zh: '有热茶吗？', en: 'Do you have warm tea?', cue: 'The assistant gestures toward the tea shelf.' }],
};
test('arbitrary explicit goal uses schema-checked mocked planner, scores generated line and rejoins', async () => {
  let observed; const { turn, logs } = setup({ planner: async (input) => { observed = input; return structuredClone(generated); } }); await turn();
  const r = await turn({ intent: 'I would like warm tea' }); assert.equal(observed.intent, 'I would like warm tea'); assert.equal(observed.returnBeat, 6); assert.equal(r.overlay.jp, generated.nodes[0].jp); assert.equal(logs.at(-1).source, 'llm-detour');
  const rejoin = await turn({ transcript: r.overlay.kana }); assert.equal(rejoin.feedback.score, 100); assert.equal(rejoin.route.kind, 'rejoin'); assert.equal(rejoin.overlay.jp, main[6].line.jp);
});
test('planner rejects malformed, cyclic anchors, overlong and branded plans without losing main state', async () => {
  for (const plan of [null, { ...generated, returnBeat: 0 }, { ...generated, nodes: Array(4).fill(generated.nodes[0]) }, { ...generated, label: 'Doraemon' }, { ...generated, nodes: [{ ...generated.nodes[0], cue: 'Teleport to another store.' }] }, { ...generated, nodes: [{ ...generated.nodes[0], jp: 'x'.repeat(501) }] }]) {
    const { turn } = setup({ planner: async () => plan }); await turn(); const r = await turn({ intent: 'I would like warm tea' }); assert.equal(r.route.kind, 'main'); assert.equal(r.overlay.jp, main[6].line.jp); assert.equal(r.advance, 'stay');
  }
  const { turn } = setup({ planner: async () => { throw new Error('Do not call'); } }); await turn(); const blocked = await turn({ intent: 'I want Doraemon' }); assert.equal(blocked.route.kind, 'main'); assert.match(blocked.feedback.notes.join(' '), /character or brand/);
});
test('failed log rolls back detour graph and reset creates fresh run IDs', async () => {
  let fail = false; const { turn, service } = setup({ log: async () => { if (fail) { fail = false; throw new Error('disk'); } } });
  const opening = await turn(); fail = true; await assert.rejects(turn({ intent: 'I want a sandwich' }));
  const retry = await turn({ intent: 'I want a sandwich' }); assert.equal(retry.route.step, 1);
  service.reset({ sessionId: 'shopping', lessonId: lesson.id }); const fresh = await turn(); assert.equal(fresh.route.kind, 'main'); assert.notEqual(fresh.route.nodeId, opening.route.nodeId);
});
test('detour planner adapter sends strict plan schema and saved anchor', async () => {
  let body; const planner = createClaudePlanner({ apiKey: 'fake', fetchImpl: async (_url, init) => { body = JSON.parse(init.body); return { ok: true, json: async () => ({ content: [{ type: 'text', text: JSON.stringify(generated) }] }) }; } });
  await planner({ lesson, current: main[6], intent: 'warm tea', returnBeat: 6, context: {} });
  assert.ok(body.output_config.format.schema.properties.nodes); assert.equal(JSON.parse(body.messages[0].content).returnAnchor.beat, 6); assert.match(body.system, /never a scene reset/);
});
test('primary bento-heating goal reaches planner instead of matching its checkout rejoin clause', async () => {
  const heated = { label: 'Heat the bento', returnBeat: 6, nodes: [{ jp: 'このお弁当を温めていただけますか。', kana: 'このおべんとうをあたためていただけますか。', romaji: 'Kono obentou o atatamete itadakemasu ka.', zh: '可以帮我加热这个便当吗？', en: 'Could you heat this bento for me?', cue: 'The assistant places the bento beside the microwave.' }] };
  for (const intent of ['我想请店员帮我把买的便当加热，然后回到结账', 'Please heat my bento, then return to checkout', 'お弁当を温めてほしい。その後レジに戻りたい']) {
    let observed; const { turn } = setup({ planner: async (input) => { observed = input.intent; return heated; } }); await turn();
    const r = await turn({ intent }); assert.equal(observed, intent); assert.equal(r.overlay.jp, heated.nodes[0].jp); assert.equal(r.route.label, 'Heat the bento');
    const resumed = await turn({ transcript: r.overlay.kana }); assert.equal(resumed.route.kind, 'rejoin'); assert.equal(resumed.overlay.jp, main[6].line.jp);
  }
});
test('return clauses do not change simple goals; non-finding actions are never substituted with shopping templates', async () => {
  const { scriptedPlan } = await import('./flow.js');
  assert.match(scriptedPlan('I want a sandwich, then return to checkout', lesson, 6).label, /sandwich/);
  assert.equal(scriptedPlan('I want warm tea, then return to checkout', lesson, 6), null);
  assert.equal(scriptedPlan('我想请店员帮我把买的便当加热，然后回到结账', lesson, 6), null);
  assert.equal(scriptedPlan('Please refund this sandwich and go back to checkout', lesson, 6), null);
});
test('planner instructions enforce learner role and primary goal; known clerk-role output is rejected', async () => {
  const { lessonSystemPrompt } = await import('./prompts.js');
  const { validatePlan } = await import('./flow.js');
  const prompt = lessonSystemPrompt(true);
  assert.match(prompt, /spoken by the LEARNER/); assert.match(prompt, /ONLY the saved RETURN ANCHOR/); assert.match(prompt, /exact primary learner goal/); assert.match(prompt, /Do not introduce unrequested products/);
  assert.throws(() => validatePlan({ ...generated, nodes: [{ ...generated.nodes[0], jp: 'お客様、ご案内いたしますね。' }] }, 6), /clerk role/);
});
