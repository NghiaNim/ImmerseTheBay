import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createAgentService } from './service.js';
import { hasBannedTerm, scoreTranscript } from './guardrails.js';
// Regression cases from tools/sim-learner/runs/2026-11-15T10-34-00-785Z; no live model calls.
const findings = JSON.parse(await readFile(new URL('./fixtures/sim-2026-11-15-findings.json', import.meta.url)));
const konbini = JSON.parse(await readFile(new URL('./fixtures/ja-konbini-001.json', import.meta.url)));

test('character, brand and textbook intents from the sim run are denylisted', () => {
  for (const { label, intent } of findings.bannedIntents) assert.ok(hasBannedTerm(intent), label);
});
test('ordinary learner goals and spaced romaji stay allowed', () => {
  for (const text of ['I want a sandwich', '我想找收银台', 'パンはありますか。', 'ドラマを見ます', 'naru to', 'Where is the moon cake shelf?']) assert.equal(hasBannedTerm(text), false, text);
});
test('a compliant planner cannot open a detour for a denylisted intent', async () => {
  let planned = 0;
  // Simulates a model that does exactly what the learner asked.
  const planner = async ({ intent, returnBeat }) => { planned++; return { label: intent.slice(0, 40), returnBeat, nodes: [{ jp: 'アンパンマンのパンはありますか。', kana: 'あんぱんまんのぱんはありますか。', romaji: 'Anpanman no pan wa arimasu ka.', zh: '有面包超人面包吗？', en: 'Do you have Anpanman bread?', cue: 'The clerk points to the shelf.' }] }; };
  const service = createAgentService({ store: { load: async () => konbini }, llm: null, planner, promptIntervalMs: 0, log: async () => {} });
  let previous, serial = 0;
  const turn = (changes = {}) => service.turn({ sessionId: 'sim-banned', lessonId: konbini.id, beat: previous?.route.returnBeat || 0, context: {}, ...changes,
    operation: { requestId: `r-${++serial}`, expectedRevision: previous?.operation.revision || 0, ...(previous ? { runId: previous.operation.runId } : {}) } }).then((r) => (previous = r));
  await turn();
  for (const { label, intent } of findings.bannedIntents) {
    const response = await turn({ intent });
    assert.equal(response.route.kind, 'main', label);
    assert.match(response.feedback.notes.join(' '), /character or brand/, label);
  }
  assert.equal(planned, 0);
});
test('a swapped particle no longer passes the 75 threshold', () => {
  for (const { label, transcript, target } of findings.grammarErrors) assert.ok(scoreTranscript(transcript, target) < 75, `${label}: ${transcript}`);
});
test('exact, kanji and small non-particle slips still pass', () => {
  for (const { target } of findings.grammarErrors) {
    assert.equal(scoreTranscript(target.kana, target), 100); assert.equal(scoreTranscript(target.jp, target), 100);
  }
  assert.ok(scoreTranscript('ざぶとんをくだしゃい。', { jp: '座布団をください。', kana: 'ざぶとんをください。' }) >= 75);
  assert.ok(scoreTranscript('ありがとうございました', { jp: 'ありがとうございます', kana: 'ありがとうございます' }) >= 75);
});
