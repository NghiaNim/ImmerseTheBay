import test from 'node:test';
import assert from 'node:assert/strict';
import { composeVisualPrompt, CHARACTER_ANCHOR, SCENE_ANCHOR, composeAmbientPrompt, AMBIENT_ANCHOR } from './continuity.js';
import { lessonSystemPrompt } from './prompts.js';

function checkBounds(prompt) {
  assert.ok(prompt.length <= 600, `length ${prompt.length}`);
  assert.ok(prompt.split(/\s+/u).length <= 95);
  assert.equal((prompt.match(/[.!?。！？]/gu) || []).length, 3);
  assert.ok(prompt.startsWith(CHARACTER_ANCHOR));
  assert.ok(prompt.endsWith(SCENE_ANCHOR));
}
test('home host and store clerk retain the same generic reference without invented appearance', () => {
  for (const action of ['The host pours tea.', 'The clerk points to the selected bottle.', 'The clerk returns attention to the checkout.']) {
    const prompt = composeVisualPrompt(action);
    checkBounds(prompt); assert.ok(prompt.includes(action));
    assert.doesNotMatch(prompt, /young woman|brown hair|blue uniform/);
    assert.match(prompt, /exact facial structure, eyes, nose and mouth/);
    assert.match(prompt, /original first-person viewpoint/);
    assert.match(prompt, /no cuts, added people or recasting/);
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
    checkBounds(prompt); assert.ok(prompt.includes('natural expressions'));
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
