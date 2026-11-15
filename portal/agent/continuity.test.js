import test from 'node:test';
import assert from 'node:assert/strict';
import { composeVisualPrompt, CHARACTER_ANCHOR, SCENE_ANCHOR, composeAmbientPrompt, AMBIENT_ANCHOR } from './continuity.js';
import { lessonSystemPrompt } from './prompts.js';

function checkBounds(prompt) {
  assert.ok(prompt.length <= 600, `length ${prompt.length}`);
  assert.ok(prompt.split(/\s+/u).length <= 95);
  assert.equal((prompt.match(/[.!?。！？]/gu) || []).length, 3);
  assert.ok(prompt.includes(CHARACTER_ANCHOR));
  assert.ok(prompt.endsWith(SCENE_ANCHOR));
}
test('home host and store clerk retain the same generic reference without invented appearance', () => {
  for (const action of ['The host pours tea.', 'The clerk points to the selected bottle.', 'The clerk returns attention to the checkout.']) {
    const prompt = composeVisualPrompt(action);
    checkBounds(prompt); assert.ok(prompt.includes(action));
    assert.doesNotMatch(prompt, /young woman|brown hair|blue uniform/);
    assert.match(prompt, /established face, hair and clothing remain unchanged/);
    assert.match(prompt, /Static eye-level medium-wide first-person POV/);
    assert.match(prompt, /locked position, orientation and focal length/);
    assert.equal(composeVisualPrompt(prompt), prompt);
  }
});
test('long, multi-sentence and no-space output preserves anchors and strict bounds', () => {
  for (const action of ['The clerk ' + 'gently '.repeat(200), 'The host pours tea. A person enters. The scene changes.', '指'.repeat(1000), '😀'.repeat(500)]) checkBounds(composeVisualPrompt(action));
});
test('explicit replacement and appearance mutation use the safe authored action', () => {
  for (const action of ['Replace the clerk with another person.', 'Change the host face.', 'A different assistant holds a bottle.', 'The clerk becomes younger.', '换脸然后拿水。', 'Cut to another store.']) {
    const prompt = composeVisualPrompt(action, 'The clerk points to the bottle.');
    checkBounds(prompt); assert.ok(prompt.includes('The clerk points to the bottle.')); assert.ok(!prompt.includes(action));
  }
});
test('invalid actions and unsafe fallbacks cannot displace anchors', () => {
  for (const action of [null, '', '\n\t', 'Doraemon enters.', 'Ｄｏｒａｅｍｏｎ enters.']) {
    const prompt = composeVisualPrompt(action, 'Replace the host face.');
    checkBounds(prompt); assert.ok(prompt.includes('natural expression'));
  }
});
test('both model modes explain identity and context precedence', () => {
  for (const mode of [false, true]) {
    const prompt = lessonSystemPrompt(mode);
    assert.match(prompt, /same face/); assert.match(prompt, /saved return checkpoint/);
    assert.match(prompt, /Never claim that a command acknowledgment/);
  }
});


test('ambient prompts preserve environmental cues with an immutable no-voice constraint', () => {
  for (const cue of ['Soft refrigeration and package rustling.', 'Rain taps the roof.', 'Quiet room ambience.']) {
    const prompt = composeAmbientPrompt(cue);
    assert.ok(prompt.startsWith(AMBIENT_ANCHOR)); assert.ok(prompt.includes(cue));
    assert.equal(composeAmbientPrompt(prompt), prompt);
  }
});
test('speech, vocal and music cues are discarded in every supported language', () => {
  for (const cue of ['The clerk says welcome.', 'Quiet store ambience and customer chatter.', 'A narrator describes the product.', 'The host whispers.', 'Soft music with no vocals.', 'A radio announcement.', '店員の話し声。', '店员说话欢迎顾客。', 'Background singing.', 'Doraemon enters.']) {
    const prompt = composeAmbientPrompt(cue);
    assert.ok(prompt.includes('Soft room ambience with gentle ventilation.'));
    assert.ok(!prompt.includes(cue));
  }
});
test('ambient bounds survive oversized, empty and invalid input', () => {
  for (const cue of [undefined, null, '', 'Soft '.repeat(500), '風'.repeat(1000), '😀'.repeat(500)]) {
    const prompt = composeAmbientPrompt(cue);
    assert.ok(prompt.startsWith(AMBIENT_ANCHOR));
    assert.ok(prompt.length <= 600); assert.ok(prompt.split(/\s+/u).length <= 95);
    assert.equal((prompt.match(/[.!?。！？]/gu) || []).length, 2);
  }
});
test('model instructions keep speech separate from video-generated ambient audio', () => {
  for (const mode of [false, true]) {
    assert.match(lessonSystemPrompt(mode), /Cartesia separately supplies the clerk voice and learner-line readout/);
    assert.match(lessonSystemPrompt(mode), /Video-generated audio is environmental ambience only/);
  }
});


test('camera drift and visible-learner actions fall back to stationary clerk cues', () => {
  const fallback = 'The clerk gestures toward the bottle while staying behind the counter.';
  for (const action of [
    'The camera moves closer to the bottle.', 'The camera pans toward the shelf.',
    'Move the camera to the shelf.', 'A close-up shows the wrapper.',
    'An over-the-shoulder view shows the clerk.', 'The visitor places a sandwich in the basket.',
    'The customer slowly turns toward the shelf.', 'Another customer enters the store.',
    'The clerk walks to the refrigerator.', 'The assistant leans toward the lens.',
    '镜头拉近瓶子。', 'カメラが近づきます。',
  ]) {
    const prompt = composeVisualPrompt(action, fallback);
    checkBounds(prompt); assert.ok(prompt.includes(fallback), action);
    assert.ok(!prompt.includes(action), action);
    assert.match(prompt, /foreground clear/);
  }
});

test('all authored shopping branches use only stationary clerk actions', async () => {
  const { scriptedPlan } = await import('./flow.js');
  for (const intent of ['I want a sandwich', 'I want a product', 'Where is the checkout counter']) {
    const plan = scriptedPlan(intent, {id:'ja-konbini-001',title:'Convenience store'}, 0);
    assert.ok(plan);
    for (const node of plan.nodes) {
      assert.match(node.cue, /same clerk stays behind the counter/);
      assert.doesNotMatch(node.cue, /visitor|learner|customer|walks|fetches|moves closer/i);
      assert.ok(composeVisualPrompt(node.cue).includes(node.cue));
    }
  }
});


test('opening can anchor approved reference appearance; follow-ups omit the world description', () => {
  const subject = 'The reference clerk with tied-back dark hair, a white shirt and beige apron stands behind the checkout';
  const action = 'The same clerk makes a small welcoming gesture';
  const opening = composeVisualPrompt(action, undefined, {initial:true, subject});
  checkBounds(opening); assert.ok(opening.includes(subject));
  const followup = composeVisualPrompt('The same clerk points with one hand', undefined, {initial:false, subject});
  checkBounds(followup); assert.ok(!followup.includes(subject));
  assert.doesNotMatch(followup, /no people|no zoom|no cuts|customer body|over-shoulder/);
  assert.equal(composeVisualPrompt(followup), followup);
  checkBounds(composeVisualPrompt('word '.repeat(300), undefined, {initial:true, subject:'subject '.repeat(300)}));
});

test('authored recipient cues remain first-person instead of summoning a visible customer', () => {
  const opening = composeVisualPrompt('The clerk behind the shop counter looks toward the learner and gives a small welcoming gesture.', '', { initial: true });
  assert.match(opening, /looks straight ahead/);
  assert.doesNotMatch(opening, /learner|visitor|customer/);
  const next = composeVisualPrompt('The clerk turns a plain wrapper toward the visitor.');
  assert.match(next, /near counter edge/);
  assert.doesNotMatch(next, /visitor/);
});

 test('speaking movement is visual only, bounded and keeps the fixed camera', () => {
  for (const action of ['The clerk nods', 'word '.repeat(300)]) {
    const p = composeVisualPrompt(action, undefined, { speaking: true });
    checkBounds(p);
    assert.match(p, /moves their lips naturally, with listening pauses/);
    assert.equal(composeVisualPrompt(p, undefined, { speaking: true }), p);
  }
  assert.doesNotMatch(composeVisualPrompt('The clerk nods'), /lips/);
  assert.match(composeAmbientPrompt('The clerk speaks'), /no speech/);
});
test('off-camera partner keeps photo environment stable through authored and dynamic branches', () => {
  for (const action of ['The clerk points at the sandwich.', 'A person smiles and moves their lips.', 'The room is quiet.', '店員がうなずく。', null]) {
    const prompt = composeVisualPrompt(action, 'The host waves at the customer.', {
      initial: true, subject: 'A visible clerk behind the counter', speaking: true, visiblePartner: false,
    });
    assert.equal(prompt, `The photographed environment retains its original layout and lighting. Existing objects remain stationary. ${SCENE_ANCHOR}`);
    assert.ok(!prompt.includes(CHARACTER_ANCHOR));
    assert.doesNotMatch(prompt, /clerk|host|speaker|face|hair|clothing|lips|mouth|customer|person\b(?! POV)/i);
    assert.ok(prompt.length <= 600); assert.ok(prompt.split(/\s+/u).length <= 95);
    assert.equal((prompt.match(/[.!?。！？]/gu) || []).length, 3);
    assert.equal(composeVisualPrompt(prompt, undefined, { visiblePartner: false }), prompt);
  }
});
