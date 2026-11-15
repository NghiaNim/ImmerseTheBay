const cancelled = () => new DOMException('Scene transition cancelled', 'AbortError');
export function transitionDelay(ms, signal, { setTimer = setTimeout, clearTimer = clearTimeout } = {}) {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) { reject(cancelled()); return; }
    const timer = setTimer(() => { signal?.removeEventListener('abort', abort); resolve(); }, ms);
    function abort() { clearTimer(timer); reject(cancelled()); }
    signal?.addEventListener('abort', abort, { once: true });
  });
}
// These are observable generation boundaries, not per-frame scene identity attestations.
export function createSceneFrameGate({ video, expectedPrompt, signal, timeoutMs = 20000, onTrace = () => {}, setTimer = setTimeout, clearTimer = clearTimeout }) {
  let started = false, startedByAck = false, firstAckChunk, observedChunks = 0, frameId, settled = false, timer, resolve, reject;
  const ready = new Promise((yes, no) => { resolve = yes; reject = no; });
  // The caller may still be awaiting SDK acknowledgements when timeout/abort fires.
  ready.catch(() => {});
  function finish(error, evidence) {
    if (settled) return; settled = true; clearTimer(timer); signal?.removeEventListener('abort', abort);
    if (frameId !== undefined) video.cancelVideoFrameCallback?.(frameId);
    onTrace({ phase: error ? 'failed' : 'decoded_frame', ...(error ? { error: error.name === 'AbortError' ? 'cancelled' : error.message } : evidence) });
    if (error) reject(error); else resolve(evidence);
  }
  function abort() { finish(cancelled()); }
  if (signal?.aborted) abort();
  else { signal?.addEventListener('abort', abort, { once: true }); timer = setTimer(() => finish(new Error('New scene video did not arrive. Restart the lesson safely.')), timeoutMs); }
  onTrace({ phase: 'waiting_for_start' });
  function message(event, source = 'broadcast') {
    if (settled) return;
    if (event?.type === 'generation_started') { started = true; if (source === 'start_ack') { startedByAck = true; firstAckChunk = undefined; } onTrace({ phase: 'generation_started', source }); return; }
    if (!event || frameId !== undefined || event.type !== 'chunk_complete') return;
    const data = event.data || {};
    if (startedByAck && firstAckChunk === undefined) firstAckChunk = data.chunk_index;
    if (observedChunks++ < 6) onTrace({ phase: 'chunk_observed', started, startedByAck, chunkIndex: data.chunk_index ?? null, framesEmitted: data.frames_emitted ?? null });
    if (!started) return;
    const firstChunk = data.chunk_index === 0 || (startedByAck && firstAckChunk === 1 && data.chunk_index === 1);
    if (!firstChunk || (Number.isFinite(data.frames_emitted) && data.frames_emitted <= 0)) return;
    const hasPrompt = typeof data.active_prompt === 'string' && data.active_prompt.length > 0;
    if (hasPrompt && expectedPrompt && data.active_prompt !== expectedPrompt) return;
    if (typeof video.requestVideoFrameCallback !== 'function') { finish(new Error('This browser cannot verify a fresh video frame. Use current Chrome.')); return; }
    const evidence = { generationStarted: true, startAcknowledged: startedByAck, chunkIndex: data.chunk_index, framesEmitted: data.frames_emitted ?? null, promptMatched: hasPrompt ? data.active_prompt === expectedPrompt : null };
    onTrace({ phase: 'waiting_for_decoded_frame', ...evidence });
    function frame(_now, metadata) {
      if (settled) return;
      // Registration occurs after the accepted chunk; this callback is a new
      // compositor submission. Do not compare presentedFrames with decoded+dropped
      // totalVideoFrames: they are different counters and can stall a valid reveal.
      finish(null, { ...evidence, decodedFrame: metadata.presentedFrames, mediaTime: metadata.mediaTime });
    }
    frameId = video.requestVideoFrameCallback(frame);
  }
  return { ready, message, cancel: abort };
}
export function presetScenePath(scene) {
  const paths = { checkout: '/lessons/images/konbini-checkout-original.png', sandwiches: '/scene-presets/konbini-sandwiches.png', drinks: '/scene-presets/konbini-drinks.png' };
  if (!scene || paths[scene.id] !== scene.startImage) throw new Error('Invalid preset scene image.');
  return paths[scene.id];
}
