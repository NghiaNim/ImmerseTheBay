import test from 'node:test';
import assert from 'node:assert/strict';
import { reviewMetrics, createGoalRecorder } from './practice-ui.js';
const flush = () => new Promise(resolve => setImmediate(resolve));
test('review distinguishes transcript match and denominators; no synthetic zero score', () => {
  const empty = Object.fromEntries(reviewMetrics({ scoredAttempts: 0, targetsAttempted: 0, targetsEncountered: 2, firstAttempt: { total: 0, rate: null } }));
  assert.equal(empty['Transcript match'], '— · no scored attempts');
  assert.equal(empty['First-attempt success'], '— · no scored targets');
  const scored = Object.fromEntries(reviewMetrics({ scoredAttempts: 3, averageTranscriptMatch: 82.5, targetsAttempted: 2, targetsEncountered: 3, firstAttempt: { total: 2, successes: 1, rate: 50 } }));
  assert.equal(scored['Transcript match'], '83% · 3 attempts');
  assert.equal(scored['First-attempt success'], '50% · 1/2 targets');
  assert.equal(scored['Targets practiced'], '2 / 3');
});
function fixture(transcribe = async () => 'Ask for directions', getUserMedia) {
  let stopped = 0; const states = [], texts = [];
  class Recorder {
    static current;
    constructor() { Recorder.current = this; this.state = 'inactive'; this.mimeType = 'audio/webm'; }
    start() { this.state = 'recording'; }
    stop() { this.state = 'inactive'; this.ondataavailable?.({ data: new Blob(['audio']) }); this.onstop?.(); }
  }
  const controller = createGoalRecorder({ Recorder, getUserMedia: getUserMedia || (async () => ({ getTracks: () => [{ stop() { stopped++; } }] })), transcribe, onState: state => states.push(state), onText: text => texts.push(text) });
  return { controller, states, texts, stopped: () => stopped };
}
test('goal recording stops mic before transcription and needs no active lesson', async () => {
  const f = fixture(); await f.controller.start(); assert.equal(f.states.at(-1), 'recording');
  f.controller.stop(); assert.equal(f.stopped(), 1); await flush();
  assert.deepEqual(f.texts, ['Ask for directions']); assert.equal(f.states.at(-1), 'idle');
});
test('cancelling permission request stops late stream and does not record', async () => {
  let resolve, stopped = 0; const f = fixture(undefined, () => new Promise(r => { resolve = r; }));
  const task = f.controller.start(); f.controller.cancel(); resolve({ getTracks: () => [{ stop() { stopped++; } }] }); await task;
  assert.equal(stopped, 1); assert.deepEqual(f.texts, []); assert.equal(f.states.at(-1), 'idle');
});
test('cancelled transcription cannot overwrite a later typed goal', async () => {
  let resolve, signal; const f = fixture((blob, ownSignal) => { signal = ownSignal; return new Promise(r => { resolve = r; }); });
  await f.controller.start(); f.controller.stop(); f.controller.cancel(); assert.equal(signal.aborted, true);
  resolve('stale goal'); await flush(); assert.deepEqual(f.texts, []);
});
