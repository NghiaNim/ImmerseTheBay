import { buildModelContext } from './model-context.js';
import { lessonSystemPrompt } from './prompts.js';
import { createOpenAIClient } from './openai.js';
import { RESPONSE_SCHEMA, PLAN_SCHEMA } from './schema.js';
export const DEFAULT_MODEL = 'claude-opus-5-5';
export function createClaudeClient({ apiKey = process.env.ANTHROPIC_API_KEY, model = process.env.ANTHROPIC_MODEL || DEFAULT_MODEL, fetchImpl = fetch, planMode = false } = {}) {
  if (!apiKey) return null;
  return async (input) => {
    const modelContext = buildModelContext(input, { planMode });
    const system = lessonSystemPrompt(planMode);
    const response = await fetchImpl('https://api.anthropic.com/v1/messages', {
      method: 'POST', signal: AbortSignal.timeout(12_000),
      headers: { 'content-type': 'application/json', 'x-api-key': apiKey, 'anthropic-version': process.env.ANTHROPIC_API_VERSION || '2026-10-21' },
      body: JSON.stringify({ model, max_tokens: planMode ? 4000 : 2500, system, output_config: { format: { type: 'json_schema', schema: planMode ? PLAN_SCHEMA : { ...RESPONSE_SCHEMA, required: Object.keys(RESPONSE_SCHEMA.properties) } } },
        messages: [{ role: 'user', content: JSON.stringify(modelContext) }] }),
    });
    if (!response.ok) throw new Error(`Claude HTTP ${response.status}`);
    const result = await response.json();
    if (result.stop_reason === 'max_tokens') throw new Error('Claude output truncated');
    const raw = result.content?.filter((block) => block.type === 'text').map((block) => block.text).join('');
    if (typeof raw !== 'string' || raw.length > 24_000) throw new Error('Invalid Claude output');
    return JSON.parse(raw);
  };
}

export function createClaudePlanner(options = {}) { return createClaudeClient({ ...options, planMode: true }); }

// An explicitly configured OpenAI key takes precedence. Provider errors are
// handled by the service's scripted fallback, never silently sent to Claude.
export function createLessonClient(options = {}) {
  return process.env.OPENAI_API_KEY?.trim()
    ? createOpenAIClient({ ...options })
    : createClaudeClient(options);
}
export function createLessonPlanner(options = {}) { return createLessonClient({ ...options, planMode: true }); }
