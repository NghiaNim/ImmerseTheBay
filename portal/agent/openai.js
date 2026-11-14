import { RESPONSE_SCHEMA, PLAN_SCHEMA } from './schema.js';
import { lessonSystemPrompt } from './prompts.js';
export const DEFAULT_OPENAI_MODEL = 'gpt-5.4-mini';
// OpenAI strict output requires every object property to be required. Route is
// optional only in our backwards-compatible HTTP response validator.
export function strictOutputSchema(schema) {
  const result = structuredClone(schema);
  function visit(node) {
    if (node.type === 'object') { node.required = Object.keys(node.properties); node.additionalProperties = false; Object.values(node.properties).forEach(visit); }
    if (node.items) visit(node.items);
    if (node.anyOf) node.anyOf.forEach(visit);
  }
  visit(result);
  return result;
}
export function createOpenAIClient({ apiKey = process.env.OPENAI_API_KEY, model = process.env.OPENAI_MODEL || DEFAULT_OPENAI_MODEL, fetchImpl = fetch, planMode = false } = {}) {
  if (!apiKey?.trim()) return null;
  const key = apiKey.trim();
  return async ({ lesson, current, transcript, context, photoCaption, fallback, attempts, intent, returnBeat }) => {
    const response = await fetchImpl('https://api.openai.com/v1/responses', {
      method: 'POST', signal: AbortSignal.timeout(12_000),
      headers: { 'content-type': 'application/json', authorization: `Bearer ${key}` },
      body: JSON.stringify({
        model, store: false, max_output_tokens: planMode ? 4000 : 2500,
        instructions: lessonSystemPrompt(planMode),
        input: JSON.stringify({ lesson: { title: lesson.title, level: lesson.level }, scenePrompt: lesson.scenePrompt, current, transcript, context, photoCaption, fallback, attempts, intent, returnBeat }),
        text: { format: { type: 'json_schema', name: planMode ? 'lesson_detour' : 'lesson_turn', strict: true, schema: strictOutputSchema(planMode ? PLAN_SCHEMA : RESPONSE_SCHEMA) } },
      }),
    });
    // Never include provider error bodies: they can contain echoed request data.
    if (!response.ok) throw new Error(`OpenAI HTTP ${response.status}`);
    const result = await response.json();
    if (result.status !== 'completed' || result.error) throw new Error('OpenAI response incomplete');
    const content = (result.output || []).filter((item) => item.type === 'message').flatMap((item) => item.content || []);
    if (content.some((item) => item.type === 'refusal')) throw new Error('OpenAI response refused');
    const raw = content.filter((item) => item.type === 'output_text').map((item) => item.text).join('');
    if (!raw || raw.length > 24_000) throw new Error('Invalid OpenAI output');
    return JSON.parse(raw);
  };
}
