import test from 'node:test';
import assert from 'node:assert/strict';
import { agentConfig, TOOLS, MODEL_ID, INSTRUCTIONS } from './config.mjs';
test('managed configuration preserves voice, no fallback, and interruptible opening', () => {
  const config = agentConfig('voice', ['a', 'b', 'c']);
  assert.equal(config.model.id, MODEL_ID);
  assert.equal(config.model.fallback_strategy, null);
  assert.equal(config.audio.output.voice_id, 'voice');
  assert.equal(config.audio.output.background_sound, null);
  assert.equal(config.initial_message_interruptions, 'allow');
  assert.deepEqual(config.language, { primary: 'ja', additional: ['en'] });
});
test('tool interface matches browser bridge and separates intent from rehearsal', () => {
  assert.deepEqual(TOOLS.map(t => [t.name, t.parameters.required]), [
    ['branch_scene', ['intent']], ['return_to_main', []], ['rehearse_line', ['transcript']],
  ]);
  for (const tool of TOOLS) {
    assert.equal(tool.type, 'client');
    assert.equal(tool.expects_response, true);
    assert.equal(tool.execution_mode, 'immediate');
    assert.ok(tool.response_timeout_secs >= 20);
  }
});
test('forbidden fallback model and incomplete resource bindings fail closed', () => {
  assert.throws(() => agentConfig('voice', ['a', 'b', 'c'], 'gpt-5.4-mini'));
  assert.throws(() => agentConfig('', ['a', 'b', 'c']));
  assert.throws(() => agentConfig('voice', ['a']));
  assert.match(INSTRUCTIONS, /video renders asynchronously/);
  assert.match(INSTRUCTIONS, /verbatim transcript/);
});
test('changed shopping goals mandate branching before spoken acknowledgement', () => {
  assert.match(INSTRUCTIONS, /call branch_scene BEFORE acknowledging/);
  assert.match(INSTRUCTIONS, /ちょっと待ってください。棚のサンドイッチを買いたいです。案内してください。/);
  assert.match(INSTRUCTIONS, /NEW practice goal takes priority over rehearsal/);
  assert.match(INSTRUCTIONS, /Never say「こちらへどうぞ」/);
});

test('voice role follows scene context and the current target remains rehearsal', () => {
 assert.match(INSTRUCTIONS, /\{\{practiceContext\}\}/);
 assert.match(INSTRUCTIONS, /current learnerLine/);
 assert.match(INSTRUCTIONS, /call rehearse_line instead/);
});
