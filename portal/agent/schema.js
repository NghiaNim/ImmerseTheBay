// Small schema validator for our closed JSON surface; no runtime package dependency.
const text = { type: 'string' };
const object = (properties, required = Object.keys(properties)) => ({ type: 'object', properties, required, additionalProperties: false });
export const PLAN_SCHEMA = object({ label: text, returnBeat: { type: 'number' }, nodes: { type: 'array', items: object({ jp: text, kana: text, romaji: text, zh: text, en: text, cue: text }) } });
export const RESPONSE_SCHEMA = object({
  visko: { type: 'array', items: { anyOf: [
    object({ name: { type: 'string', enum: ['set_prompt', 'set_audio_prompt'] }, data: object({ prompt: text }) }),
    object({ name: { type: 'string', enum: ['set_resolution'] }, data: object({ resolution: text }) }),
    object({ name: { type: 'string', enum: ['start', 'pause', 'resume', 'reset'] }, data: object({}) }),
  ] } },
  overlay: { anyOf: [{ type: 'null' }, object({ jp: text, kana: text, romaji: text, zh: text, en: text, hintLevel: { type: 'number' } })] },
  feedback: { anyOf: [{ type: 'null' }, object({ score: { type: 'number' }, notes: { type: 'array', items: text } })] },
  advance: { type: 'string', enum: ['stay', 'next_line', 'scene_done'] },
  route: object({ kind: { type: 'string', enum: ['main', 'detour', 'rejoin'] }, nodeId: text, label: text, returnBeat: { type: 'number' }, step: { type: 'number' }, total: { type: 'number' } }),
}, ['visko', 'overlay', 'feedback', 'advance']);
export function validateSchema(value, schema = RESPONSE_SCHEMA) {
  if (schema.anyOf) return schema.anyOf.some((s) => validateSchema(value, s));
  if (schema.enum) return schema.enum.includes(value);
  if (schema.type === 'null') return value === null;
  if (schema.type === 'number') return typeof value === 'number' && Number.isFinite(value);
  if (schema.type === 'string') return typeof value === 'string';
  if (schema.type === 'array') return Array.isArray(value) && value.length <= 20 && value.every((v) => validateSchema(v, schema.items));
  if (schema.type === 'object') return value !== null && typeof value === 'object' && !Array.isArray(value)
    && schema.required.every((k) => Object.hasOwn(value, k))
    && Object.keys(value).every((k) => Object.hasOwn(schema.properties, k) && validateSchema(value[k], schema.properties[k]));
  return false;
}
