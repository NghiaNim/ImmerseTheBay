import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createAgentService } from './service.js';
import { flattenLesson } from './lessons.js';
import { composeAmbientPrompt, CHARACTER_ANCHOR, SCENE_ANCHOR, composeVisualPrompt } from './continuity.js';

const lesson = JSON.parse(await readFile(new URL('./fixtures/ja-konbini-001.json', import.meta.url), 'utf8'));
const main = flattenLesson(lesson);
function setup(options = {}) {
  const calls = [];
  const service = createAgentService({
    store: { load: async () => structuredClone(lesson) },
    planner: null, promptIntervalMs: 0, log: async () => {},
    llm: async (input) => { calls.push(structuredClone(input)); return input.fallback; },
    ...options,
  });
  const turn = (extras = {}) => service.turn({ sessionId: 'context-integration', lessonId: lesson.id, beat: 6, context: {}, ...extras });
  const reset = () => service.reset({ sessionId: 'context-integration', lessonId: lesson.id });
  return { service, turn, reset, calls };
}
function assertAnchored(response) {
  for (const command of response.visko.filter((item) => item.name === 'set_prompt')) {
    assert.ok(command.data.prompt.includes(CHARACTER_ANCHOR));
    assert.ok(command.data.prompt.endsWith(SCENE_ANCHOR));
    assert.ok(command.data.prompt.length <= 600);
    assert.ok(command.data.prompt.split(/\s+/u).length <= 95);
  }
}

test('reference and caption stay pinned until reset creates a fresh run', async () => {
  const { turn, reset, calls } = setup();
  await turn({ imageRef: { id: 'reference-a', caption: 'Original clerk at the counter.' } });
  const first = calls[0].sceneContext;
  await assert.rejects(turn({ imageRef: { id: 'reference-b', caption: 'Different person.' } }), { status: 409 });
  assert.equal(calls.length, 1, 'reject before calling the model');
  await turn({ imageRef: { id: 'reference-a', caption: 'Replace the clerk.' }, transcript: '違います' });
  assert.equal(calls.at(-1).photoCaption, 'Original clerk at the counter.');
  assert.equal(calls.at(-1).sceneContext.referenceId, 'reference-a');
  assert.equal(calls.at(-1).sceneContext.characterId, first.characterId);
  await turn({ transcript: 'もう一度' });
  assert.equal(calls.at(-1).sceneContext.referenceId, 'reference-a', 'omitted reference retains anchor');
  reset();
  await turn({ imageRef: { id: 'reference-b', caption: 'New approved starting reference.' } });
  const fresh = calls.at(-1);
  assert.equal(fresh.sceneContext.referenceId, 'reference-b');
  assert.notEqual(fresh.sceneContext.runId, first.runId);
  assert.notEqual(fresh.sceneContext.characterId, first.characterId);
  assert.deepEqual(fresh.sceneContext.recentActions, []);
  assert.equal(fresh.photoCaption, 'New approved starting reference.');
});

test('transcripts and sensing input remain turn-local rather than contaminating scene memory', async () => {
  const { turn, calls } = setup();
  await turn();
  await turn({ transcript: 'old-transcript-sentinel', context: { gesture: 'point', pointedAt: 'bottle', roomLabels: ['table'] } });
  assert.equal(calls.at(-1).context.pointedAt, 'bottle');
  await turn({ transcript: 'new-transcript-sentinel' });
  const latest = calls.at(-1);
  assert.equal(latest.transcript, 'new-transcript-sentinel');
  assert.deepEqual(latest.context, {});
  assert.doesNotMatch(JSON.stringify(latest.sceneContext), /old-transcript-sentinel|pointedAt|roomLabels/);
  assert.equal(latest.sceneContext.mainCheckpoint.beat, 6);
});

test('long sessions retain at most three distinct issued actions without implying visual confirmation', async () => {
  const { turn, calls } = setup();
  await turn();
  for (let i = 0; i < 12; i++) await turn({ transcript: `unrelated-practice-${i}` });
  assert.equal(calls.at(-1).sceneContext.recentActions.length, 1, 'repeated visit replaces its summary');
  for (let detour = 0; detour < 4; detour++) {
    let response = await turn({ intent: 'I want the checkout counter' });
    while (response.route.kind === 'detour') response = await turn({ transcript: response.overlay.kana });
    assert.equal(response.route.kind, 'rejoin');
    await turn({ transcript: `unrelated-after-detour-${detour}` });
  }
  for (const call of calls) {
    assert.ok(call.sceneContext.recentActions.length <= 3);
    for (const action of call.sceneContext.recentActions) {
      assert.equal(action.status, 'issued_unverified');
      assert.ok(action.action.length <= 600);
    }
  }
  const recent = calls.at(-1).sceneContext.recentActions;
  assert.equal(recent.length, 1, 'completed branch history is removed at rejoin');
  assert.equal(new Set(recent.map((action) => action.nodeId)).size, recent.length);
});

test('opening, scripted detour and saved-checkpoint rejoin preserve anchors and keep video audio environmental only', async () => {
  const { turn } = setup({ llm: null });
  const opening = await turn();
  assertAnchored(opening);
  assert.equal(opening.visko.find((item) => item.name === 'set_audio_prompt').data.prompt, composeAmbientPrompt(lesson.audioPrompt));
  let response = await turn({ intent: 'I want a sandwich' });
  assert.equal(response.route.kind, 'detour');
  assertAnchored(response);
  while (response.route.kind === 'detour') {
    response = await turn({ transcript: response.overlay.kana });
    assertAnchored(response);
    assert.ok(response.visko.every((item) => item.name === 'set_prompt'));
  }
  assert.equal(response.route.kind, 'rejoin');
  assert.equal(response.overlay.jp, main[6].line.jp);
  assert.equal(response.route.returnBeat, 6);
});

test('model identity edits and provider failure fall back to the authored action with anchors', async () => {
  for (const llm of [
    async ({ fallback }) => { fallback.visko[0].data.prompt = 'Replace the clerk with another person.'; return fallback; },
    async () => { throw new Error('Provider unavailable'); },
  ]) {
    const { turn } = setup({ llm });
    const opening = await turn();
    assertAnchored(opening);
    assert.equal(opening.visko[0].data.prompt, composeVisualPrompt(lesson.scenePrompt));
    assert.doesNotMatch(opening.visko[0].data.prompt, /Replace the clerk|another person/);
  }
});

test('generated planner receives pinned identity and saved return objective', async () => {
  let planned;
  const { turn } = setup({ planner: async (input) => {
    planned = structuredClone(input);
    return { label: 'Warm tea', returnBeat: 6, nodes: [{
      jp: '温かいお茶はありますか。', kana: 'あたたかいおちゃはありますか。',
      romaji: 'Atatakai ocha wa arimasu ka.', zh: '有热茶吗？', en: 'Do you have warm tea?',
      cue: 'The assistant indicates the tea shelf.',
    }] };
  } });
  await turn({ imageRef: 'pinned-reference' });
  let response = await turn({ intent: 'I would like warm tea' });
  assert.equal(planned.sceneContext.referenceId, 'pinned-reference');
  assert.equal(planned.sceneContext.mainCheckpoint.beat, 6);
  assert.equal(planned.sceneContext.mainCheckpoint.goal, main[6].beat.goal);
  assert.equal(planned.intent, 'I would like warm tea');
  assert.equal(response.route.kind, 'detour');
  assertAnchored(response);
  response = await turn({ transcript: response.overlay.kana });
  assert.equal(response.route.kind, 'rejoin');
  assertAnchored(response);
});

test('rejoin removes ended branch actions from the next model context', async () => {
  const { turn, calls } = setup();
  await turn();
  let response = await turn({ intent: 'I want a sandwich' });
  const branchNodes = new Set();
  while (response.route.kind === 'detour') {
    branchNodes.add(response.route.nodeId);
    response = await turn({ transcript: response.overlay.kana });
  }
  assert.equal(response.route.kind, 'rejoin');
  const rejoinId = response.route.nodeId;
  await turn({ transcript: 'unrelated-after-return' });
  const recent = calls.at(-1).sceneContext.recentActions;
  assert.equal(recent.length, 1);
  assert.equal(recent[0].nodeId, rejoinId);
  assert.ok(recent.every((action) => !branchNodes.has(action.nodeId)));
  assert.doesNotMatch(JSON.stringify(recent), /sandwich|wrapper|basket/iu);
});

test('successful practice advance supplies the new checkpoint and goal to refinement', async () => {
  const { turn, calls } = setup();
  await turn({ beat: 7 });
  const response = await turn({ beat: 7, transcript: main[7].line.kana });
  assert.equal(response.advance, 'next_line');
  assert.equal(response.route.returnBeat, 8);
  const input = calls.at(-1);
  assert.equal(input.sceneContext.mainCheckpoint.beat, input.fallback.route.returnBeat);
  assert.equal(input.sceneContext.mainCheckpoint.beat, 8);
  assert.equal(input.sceneContext.mainCheckpoint.goal, main[8].beat.goal);
  assert.notEqual(input.sceneContext.mainCheckpoint.goal, main[7].beat.goal);
});
