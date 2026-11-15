import test from 'node:test';
import assert from 'node:assert/strict';
import { createSceneFrameGate, transitionDelay, presetScenePath } from './scene-transition.js';
function fixture() {
  let callback, timeout, cancelled = 0, clears = 0;
  const controller = new AbortController();
  const video = { requestVideoFrameCallback(fn) { callback = fn; return 8; }, cancelVideoFrameCallback() { cancelled++; }, getVideoPlaybackQuality() { return { totalVideoFrames: 10 }; } };
  const gate = createSceneFrameGate({ video, expectedPrompt: 'new scene', signal: controller.signal, setTimer(fn) { timeout = fn; return 3; }, clearTimer() { clears++; } });
  return { gate, controller, frame: () => callback?.(0, { presentedFrames: 11, mediaTime: 2 }), hasFrame: () => !!callback, timeout: () => timeout(), counts: () => ({ cancelled, clears }) };
}
test('new scene requires start boundary, first emitted chunk, then decoded frame', async () => {
  const f = fixture(); let ready = false; f.gate.ready.then(() => { ready = true; });
  f.gate.message({ type: 'chunk_complete', data: { chunk_index: 0 } }); assert.equal(f.hasFrame(), false);
  f.gate.message({ type: 'generation_started' });
  for (const data of [{ chunk_index: 3 }, { chunk_index: 0, frames_emitted: 0 }, { chunk_index: 0, active_prompt: 'old scene' }]) f.gate.message({ type: 'chunk_complete', data });
  assert.equal(f.hasFrame(), false);
  f.gate.message({ type: 'chunk_complete', data: { chunk_index: 0, frames_emitted: 64, active_prompt: 'new scene' } });
  await Promise.resolve(); assert.equal(ready, false); assert.equal(f.hasFrame(), true);
  f.frame(); const evidence = await f.gate.ready; assert.equal(evidence.promptMatched, true); assert.equal(evidence.decodedFrame, 11);
});
test('deployment without prompt telemetry records weaker evidence without inventing it', async () => {
  const f = fixture(); f.gate.message({ type: 'generation_started' }); f.gate.message({ type: 'chunk_complete', data: { chunk_index: 0 } }); f.frame();
  const evidence = await f.gate.ready; assert.equal(evidence.promptMatched, null); assert.equal(evidence.framesEmitted, null);
});
test('Leave cancels frame wait and late frames never complete the transition', async () => {
  const f = fixture(); f.gate.message({ type: 'generation_started' }); f.gate.message({ type: 'chunk_complete', data: { chunk_index: 0 } });
  f.controller.abort(); await assert.rejects(f.gate.ready, { name: 'AbortError' }); f.frame(); assert.deepEqual(f.counts(), { cancelled: 1, clears: 1 });
});
test('video verification has a bounded timeout and no unverified reveal', async () => {
  const f = fixture(); f.timeout(); await assert.rejects(f.gate.ready, /did not arrive/); assert.equal(f.hasFrame(), false);
});
test('fade delay is cancellable and image paths cannot escape preset allowlist', async () => {
  const controller = new AbortController(); let cleared = false;
  const wait = transitionDelay(300, controller.signal, { setTimer: () => 1, clearTimer: () => { cleared = true; } }); controller.abort();
  await assert.rejects(wait, { name: 'AbortError' }); assert.equal(cleared, true);
  assert.equal(presetScenePath({ id: 'drinks', startImage: '/scene-presets/konbini-drinks.png' }), '/scene-presets/konbini-drinks.png');
  assert.throws(() => presetScenePath({ id: 'drinks', startImage: 'https://outside.invalid/image.png' }));
});

test('correlated start acknowledgement admits observed one-based first chunk without broadcast', async () => {
  const f = fixture();
  f.gate.message({ type: 'generation_started' }, 'start_ack');
  f.gate.message({ type: 'chunk_complete', data: { chunk_index: 1, frames_emitted: 33 } });
  assert.equal(f.hasFrame(), true); f.frame();
  const evidence = await f.gate.ready; assert.equal(evidence.chunkIndex, 1); assert.equal(evidence.startAcknowledged, true);
});
test('one-based chunk needs acknowledgement and must be the first chunk observed after it', async () => {
  const f = fixture(); f.gate.message({ type: 'generation_started' });
  f.gate.message({ type: 'chunk_complete', data: { chunk_index: 1 } }); assert.equal(f.hasFrame(), false);
  f.gate.message({ type: 'generation_started' }, 'start_ack');
  f.gate.message({ type: 'chunk_complete', data: { chunk_index: 8 } });
  f.gate.message({ type: 'chunk_complete', data: { chunk_index: 1 } }); assert.equal(f.hasFrame(), false);
  f.controller.abort(); await assert.rejects(f.gate.ready, { name: 'AbortError' });
});
