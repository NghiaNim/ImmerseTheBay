import test from 'node:test';
import assert from 'node:assert/strict';
import { createOpenAIClient, DEFAULT_OPENAI_MODEL, strictOutputSchema } from './openai.js';
import { createLessonClient, createLessonPlanner } from './llm.js';
import { RESPONSE_SCHEMA } from './schema.js';
import { createAgentService } from './service.js';
const line = { jp: 'お願いします。', kana: 'おねがいします。', romaji: 'Onegaishimasu.', zh: '麻烦您了。', en: 'Please.', cue: 'The assistant holds out a paper bag.' };
const lesson = { id: 'ja-konbini-test', title: 'Shop', level: 'N1', scenePrompt: 'An assistant stands at a shop counter.', audioPrompt: 'Quiet shop ambience.', startImage: '', beats: [{ goal: 'Shop', lines: [line], vocab: [] }] };
const input = { lesson, current: { line }, context: {}, fallback: {}, intent: 'I would like warm tea', returnBeat: 0 };
const success = (value) => ({ ok: true, json: async () => ({ status: 'completed', output: [{ type: 'reasoning', summary: [] }, { type: 'message', content: [{ type: 'output_text', text: JSON.stringify(value) }] }] }) });
test('OpenAI Responses request uses Bearer auth and strict text.format without storing', async () => {
  let captured;
  const client = createOpenAIClient({ apiKey: 'sk-fixture-not-real', fetchImpl: async (url, init) => { captured = { url, init, body: JSON.parse(init.body) }; return success({ valid: true }); } });
  assert.deepEqual(await client(input), { valid: true });
  assert.equal(captured.url, 'https://api.openai.com/v1/responses'); assert.equal(captured.init.headers.authorization, 'Bearer sk-fixture-not-real');
  assert.equal(captured.body.model, DEFAULT_OPENAI_MODEL); assert.equal(captured.body.store, false); assert.equal(captured.body.text.format.type, 'json_schema'); assert.equal(captured.body.text.format.strict, true);
  assert.ok(captured.body.text.format.schema.required.includes('route')); assert.ok(!JSON.stringify(captured.body).includes('sk-fixture'));
  const strict = strictOutputSchema(RESPONSE_SCHEMA);
  function check(node) { if (node.type === 'object') { assert.deepEqual(node.required, Object.keys(node.properties)); assert.equal(node.additionalProperties, false); Object.values(node.properties).forEach(check); } if (node.items) check(node.items); if (node.anyOf) node.anyOf.forEach(check); }
  check(strict);
});
test('OpenAI plans use bounded plan schema, saved anchor and continuity instructions', async () => {
  let body; const client = createOpenAIClient({ apiKey: 'sk-test', model: 'explicit-model', planMode: true, fetchImpl: async (_url, init) => { body = JSON.parse(init.body); return success({ label: 'Tea', returnBeat: 0, nodes: [line] }); } });
  await client(input); assert.equal(body.model, 'explicit-model'); assert.equal(body.text.format.name, 'lesson_detour'); assert.ok(body.text.format.schema.properties.nodes); assert.equal(JSON.parse(body.input).returnAnchor.beat, 0); assert.match(body.instructions, /never a scene reset/);
});
test('HTTP errors, incomplete output, refusals and malformed JSON reject without secret echo', async () => {
  for (const result of [
    { ok: false, status: 401, json: async () => ({ secret: 'key-do-not-log' }) },
    { ok: true, json: async () => ({ status: 'incomplete', output: [] }) },
    { ok: true, json: async () => ({ status: 'completed', output: [{ type: 'message', content: [{ type: 'refusal', refusal: 'private detail' }] }] }) },
    { ok: true, json: async () => ({ status: 'completed', output: [{ type: 'message', content: [{ type: 'output_text', text: '{bad' }] }] }) },
  ]) {
    const client = createOpenAIClient({ apiKey: 'sk-key-do-not-log', fetchImpl: async () => result });
    await assert.rejects(client(input), (e) => !/key-do-not-log|private detail/.test(e.message));
  }
});
test('OpenAI env key wins for turns and planning; provider failure uses script, not Claude', async () => {
  const saved = { openai: process.env.OPENAI_API_KEY, model: process.env.OPENAI_MODEL, claude: process.env.ANTHROPIC_API_KEY };
  process.env.OPENAI_API_KEY = 'sk-openai-fixture'; process.env.OPENAI_MODEL = 'custom-openai'; process.env.ANTHROPIC_API_KEY = 'claude-fixture';
  try {
    const urls = [];
    const options = { fetchImpl: async (url, init) => { urls.push(url); assert.equal(JSON.parse(init.body).model, 'custom-openai'); return { ok: false, status: 429 }; } };
    const llm = createLessonClient(options); const planner = createLessonPlanner(options);
    const logs = []; const service = createAgentService({ store: { load: async () => lesson }, llm, planner, promptIntervalMs: 0, log: async (e) => logs.push(e) });
    let r = await service.turn({ sessionId: 'openai-test', lessonId: lesson.id, beat: 0, context: {} }); assert.equal(r.overlay.jp, line.jp); assert.equal(logs[0].source, 'scripted');
    r = await service.turn({ sessionId: 'openai-test', lessonId: lesson.id, beat: 0, context: {}, intent: 'I would like warm tea' }); assert.equal(r.route.kind, 'main');
    assert.deepEqual(urls, ['https://api.openai.com/v1/responses', 'https://api.openai.com/v1/responses']); assert.doesNotMatch(JSON.stringify(logs), /sk-openai-fixture|claude-fixture/);
  } finally {
    for (const [env, value] of [['OPENAI_API_KEY', saved.openai], ['OPENAI_MODEL', saved.model], ['ANTHROPIC_API_KEY', saved.claude]]) { if (value === undefined) delete process.env[env]; else process.env[env] = value; }
  }
});
test('requested Luna default disables reasoning; older explicit overrides receive no reasoning parameter', async () => {
  for (const [model, expected] of [[DEFAULT_OPENAI_MODEL, { effort: 'none' }], ['gpt-4o', undefined]]) {
    let body; const client = createOpenAIClient({ apiKey: 'sk-fake', model, reasoningEffort: 'none', fetchImpl: async (_url, init) => { body = JSON.parse(init.body); return success({}); } });
    await client(input); assert.equal(body.model, model); assert.deepEqual(body.reasoning, expected);
  }
  assert.equal(DEFAULT_OPENAI_MODEL, 'gpt-6-luna');
});
