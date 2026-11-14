import { randomUUID } from 'node:crypto';
import { hasBannedTerm, safePrompt, scoreTranscript } from './guardrails.js';
import { validateSchema, PLAN_SCHEMA } from './schema.js';
const line = (jp, kana, romaji, zh, en, cue) => ({ jp, kana, romaji, zh, en, cue });
// Original conversational content; a detour changes the target overlay, not just the video prompt.
const sandwich = [
  line('すみません、予定を変えてサンドイッチを見たいんですが、どちらにありますか。', 'すみません、よていをかえてさんどいっちをみたいんですが、どちらにありますか。', 'Sumimasen, yotei o kaete sandoicchi o mitai n desu ga, dochira ni arimasu ka.', '不好意思，我想改一下主意，看看三明治，请问在哪边？', 'Excuse me, I have changed my mind and would like to look at the sandwiches. Where are they?', 'The shop assistant points toward the refrigerated sandwich shelf.'),
  line('こちらのサンドイッチは何が入っていますか。ほかの種類もまだありますか。', 'こちらのさんどいっちはなにがはいっていますか。ほかのしゅるいもまだありますか。', 'Kochira no sandoicchi wa nani ga haitte imasu ka. Hoka no shurui mo mada arimasu ka.', '这个三明治里面有什么？其他种类现在还有吗？', 'What is in this sandwich? Do you still have any other kinds?', 'The assistant turns a plain sandwich wrapper toward the visitor.'),
  line('では、こちらを一つお願いします。かごに入れておきますね。', 'では、こちらをひとつおねがいします。かごにいれておきますね。', 'Dewa, kochira o hitotsu onegaishimasu. Kago ni irete okimasu ne.', '那就要这个一个吧。我先把它放进购物篮。', 'Then I will take one of these. I will put it in my basket.', 'The visitor places the selected sandwich in the shopping basket.'),
];
const product = [
  line('すみません、探している商品があるんですが、売り場を教えていただけますか。', 'すみません、さがしているしょうひんがあるんですが、うりばをおしえていただけますか。', 'Sumimasen, sagashite iru shouhin ga aru n desu ga, uriba o oshiete itadakemasu ka.', '不好意思，我在找一种商品，可以告诉我在哪个货架吗？', 'Excuse me, I am looking for an item. Could you show me its section?', 'The assistant gestures toward a nearby shelf.'),
  line('こちらの商品について、少し伺ってもよろしいでしょうか。', 'こちらのしょうひんについて、すこしうかがってもよろしいでしょうか。', 'Kochira no shouhin ni tsuite, sukoshi ukagatte mo yoroshii deshou ka.', '我可以问一下这个商品的情况吗？', 'May I ask you a little about this item?', 'The assistant lifts a plain package from the shelf.'),
  line('ありがとうございます。こちらを一つ、かごに入れておきます。', 'ありがとうございます。こちらをひとつ、かごにいれておきます。', 'Arigatou gozaimasu. Kochira o hitotsu, kago ni irete okimasu.', '谢谢。我先把这个放一个进购物篮。', 'Thank you. I will put one of these in my basket.', 'The visitor puts the package into the basket.'),
];
const counter = [
  line('すみません、お会計はこちらでよろしいでしょうか。', 'すみません、おかいけいはこちらでよろしいでしょうか。', 'Sumimasen, okaikei wa kochira de yoroshii deshou ka.', '不好意思，是在这里结账吗？', 'Excuse me, is this where I pay?', 'The assistant gestures toward the checkout counter.'),
  line('ありがとうございます。こちらで順番を待ちます。', 'ありがとうございます。こちらでじゅんばんをまちます。', 'Arigatou gozaimasu. Kochira de junban o machimasu.', '谢谢。我在这里等候。', 'Thank you. I will wait here for my turn.', 'The visitor sets the basket beside the checkout counter.'),
];
export function detectedIntent(request, target) {
  if (request.intent?.trim()) return request.intent.trim();
  const transcript = request.transcript?.trim();
  // An off-target practice attempt is not automatically a goal. Require explicit
  // desire/search wording AND a concrete shopping target, with low target similarity.
  if (!transcript || scoreTranscript(transcript, target) >= 50) return null;
  const desire = /\b(i want|i would like|i'd like|looking for|where can i find)\b|我想|我要|想买|想買|买一个|買一個|買いたい|探しています|ほしい|欲しい/iu;
  const item = /sandwich|サンドイッチ|さんどいっち|三明治|counter|checkout|レジ|れじ|收银|收銀|商品|product|item|货架|貨架/iu;
  return desire.test(transcript) && item.test(transcript) ? transcript : null;
}
export function scriptedPlan(intent, lesson, returnBeat) {
  if (!/konbini|convenience|コンビニ/iu.test(lesson.id + ' ' + lesson.title)) return null;
  if (/sandwich|サンドイッチ|さんどいっち|三明治/iu.test(intent)) return { label: 'サンドイッチを選ぶ · Choose a sandwich', returnBeat, nodes: structuredClone(sandwich) };
  if (/counter|checkout|レジ|れじ|收银|收銀|结账|結帳/iu.test(intent)) return { label: 'レジを探す · Find the counter', returnBeat, nodes: structuredClone(counter) };
  if (/product|item|shelf|商品|货架|貨架/iu.test(intent)) return { label: '商品を探す · Find an item', returnBeat, nodes: structuredClone(product) };
  return null;
}
export function validatePlan(plan, returnBeat) {
  if (!validateSchema(plan, PLAN_SCHEMA) || plan.returnBeat !== returnBeat || plan.nodes.length < 1 || plan.nodes.length > 3 || plan.label.length > 100 || !plan.label.trim() || hasBannedTerm(JSON.stringify(plan))) throw new Error('Unsafe detour plan');
  const nodes = plan.nodes.map((node) => {
    if (Object.values(node).some((v) => !v.trim() || v.length > 500)) throw new Error('Invalid detour line');
    if (/cut to|teleport|new scene|another store|different store|scene reset|restart the scene/iu.test(node.cue)) throw new Error('Detour breaks scene continuity');
    return { ...node, cue: safePrompt(node.cue, 'The assistant waits beside the shelf.') };
  });
  return { ...plan, nodes };
}
export function initializeFlow(state, beat) {
  if (state.flow) return;
  state.flow = { runId: randomUUID(), nextVisit: 0, mainBeat: beat, detoursUsed: 0, active: null, nodes: [], edges: [], current: null };
  visit(state.flow, 'main', beat, 'Lesson checkpoint');
}
function visit(flow, kind, beat, label, step = 0, total = 0) {
  const node = { kind, nodeId: `${flow.runId}:visit-${flow.nextVisit++}`, label, returnBeat: beat, step, total };
  if (flow.current) flow.edges.push({ from: flow.current.nodeId, to: node.nodeId });
  flow.nodes.push(node); flow.current = node;
  // Keep only a bounded trace; IDs remain monotonic even after trace eviction.
  if (flow.nodes.length > 100) flow.nodes.shift();
  if (flow.edges.length > 100) flow.edges.shift();
  return node;
}
export function startDetour(state, plan) {
  const flow = state.flow;
  flow.detoursUsed++;
  flow.active = { ...plan, index: 0 };
  state.hintLevel = 0;
  visit(flow, 'detour', flow.mainBeat, plan.label, 1, plan.nodes.length);
}
export function detourTurn(request, state, mainTarget, entered = false) {
  const flow = state.flow;
  const active = flow.active;
  const current = active.nodes[active.index];
  const c = request.context;
  const hint = c.gesture === 'long_pinch' || /hint|repeat/i.test(c.glassesKey || '');
  const point = c.gesture === 'point';
  const transcript = entered || request.intent ? '' : request.transcript?.trim() || '';
  const score = transcript ? scoreTranscript(transcript, current) : null;
  const move = !entered && !request.intent && !hint && !point && (c.gesture === 'pinch' || c.glassesKey === 'next' || score !== null && score >= 75);
  const notes = [];
  if (entered) notes.push('Follow this short side interaction; your main lesson checkpoint is saved.');
  else if (request.intent) notes.push('Finish this side interaction before starting another; your main checkpoint is still saved.');
  if (transcript) notes.push('Text similarity only; this does not measure pronunciation.', score >= 75 ? 'Good match. Continue with the next interaction.' : 'Try the current side-interaction line again.');
  if (hint || c.gaze === 'room' || transcript && score < 75) state.hintLevel = Math.min(3, state.hintLevel + 1);
  if (hint) notes.push(`Cue: ${current.cue}`);
  if (move) {
    state.hintLevel = 0;
    active.index++;
    if (active.index === active.nodes.length) {
      flow.active = null;
      visit(flow, 'rejoin', flow.mainBeat, 'Back to the saved lesson checkpoint');
      notes.push('Side interaction complete. Continue from your saved main lesson line.');
      return { line: mainTarget.line, cue: mainTarget.line.cue, score, notes, current };
    }
    visit(flow, 'detour', flow.mainBeat, active.label, active.index + 1, active.nodes.length);
  }
  return { line: active.nodes[active.index], cue: active.nodes[active.index].cue, score, notes, current };
}
export function moveMain(state, nextBeat) {
  state.flow.mainBeat = nextBeat;
  visit(state.flow, 'main', nextBeat, 'Lesson checkpoint');
}
