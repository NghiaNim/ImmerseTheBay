import test from 'node:test';
import assert from 'node:assert/strict';
import { createDialogueTranslator } from './translate.js';

const result = (text) => Response.json({
  status: 'completed',
  output: [{ type: 'message', content: [{ type: 'output_text', text }] }],
});

test('caption provider returns both fields in one structured request', async () => {
  const original = '少々お待ちください。';
  let calls = 0;
  const translate = createDialogueTranslator({ apiKey: 'test-key', fetchImpl: async (_url, request) => {
    calls++;
    const body = JSON.parse(request.body);
    assert.equal(body.input, original);
    assert.equal(body.store, false);
    assert.deepEqual(body.text.format.schema.required, ['english', 'romaji']);
    assert.equal(body.text.format.strict, true);
    return result(JSON.stringify({ english: 'One moment, please.', romaji: 'Shōshō omachi kudasai.' }));
  } });
  assert.deepEqual(await translate({ text: original, includeRomaji: true }), {
    english: 'One moment, please.',
    romaji: 'Shōshō omachi kudasai.',
  });
  assert.equal(calls, 1);
});

test('invalid romaji leaves the successful translation available', async () => {
  for (const invalid of ['日本語', '•••', '<b>Romaji</b>', '', 'a'.repeat(4001)]) {
    const translate = createDialogueTranslator({ apiKey: 'test-key', fetchImpl: async () => result(JSON.stringify({ english: 'Welcome.', romaji: invalid })) });
    assert.deepEqual(await translate({ text: 'いらっしゃいませ。', includeRomaji: true }), { english: 'Welcome.', romaji: '' });
  }
});

test('English-only translator keeps a plain-text provider request', async () => {
  const translate = createDialogueTranslator({ apiKey: 'test-key', fetchImpl: async (_url, request) => {
    const body = JSON.parse(request.body);
    assert.equal(body.text, undefined);
    return result('Welcome.');
  } });
  assert.equal(await translate({ text: 'いらっしゃいませ。' }), 'Welcome.');
});
