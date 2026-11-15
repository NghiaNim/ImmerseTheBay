import { randomBytes } from 'node:crypto';
import { AgentError, validateLesson } from '../agent/lessons.js';
import { hasBannedTerm, safePrompt } from '../agent/guardrails.js';
import { strictOutputSchema, DEFAULT_OPENAI_MODEL } from '../agent/openai.js';
// Learner request (voice transcript or typed, any language) → a complete original
// lesson in the normal lesson shape. Only text goes to the model; photos stay local
// and are used solely as the Visko start image.
export const LEVELS = ['N5', 'N4', 'N3', 'N2', 'N1'];
const text = { type: 'string' };
const object = (properties) => ({ type: 'object', properties, required: Object.keys(properties), additionalProperties: false });
const utterance = { jp: text, kana: text, romaji: text, zh: text, en: text };
export const SCENARIO_SCHEMA = object({
  title: text,
  partnerRole: object({ ja: text, en: text }),
  scenePrompt: text,
  audioPrompt: text,
  beats: { type: 'array', items: object({
    goal: text,
    lines: { type: 'array', items: object({ partner: object(utterance), ...utterance, cue: text, alternatives: { type: 'array', items: object(utterance) } }) },
    vocab: { type: 'array', items: object({ jp: text, kana: text, en: text, objectHint: text }) },
  }) },
});

export const SCENARIO_INSTRUCTIONS = [
  'You write short, original Japanese conversation-practice scenarios for one learner. The input JSON has the learner\'s request (any language), a JLPT level and whether a photo of the real place will be the video backdrop.',
  'The scenario is a face-to-face conversation between the learner and ONE adult conversation partner who stands or sits in front of the learner. partnerRole names that person in Japanese and English (for example 店員 / shop clerk, 駅員 / station staff, 同僚 / coworker).',
  'Each line is one exchange: partner = what the partner says first, then jp = the learner\'s proposed answer. Every jp line is spoken by the LEARNER; never give the learner the partner\'s role. The first line may have an empty partner (all partner fields empty strings) when the learner opens the conversation; every other line must have a partner utterance. alternatives = 1 or 2 other natural learner answers that fit the same moment at the same level.',
  'Keep it short: 1 or 2 beats with 3 or 4 lines in total. Each beat has a short English goal. The conversation follows the learner\'s request, resolves it, and ends politely.',
  'Match the JLPT level: N5/N4 short polite sentences with basic vocabulary; N3 everyday conversational grammar; N2/N1 natural, more nuanced keigo and expressions. Use natural modern Japanese.',
  'Field rules for every utterance: jp = natural Japanese; kana = the complete reading of jp in hiragana (katakana loanwords may stay katakana), with NO kanji, NO romaji and the same punctuation; romaji = Hepburn romanization; zh = Simplified Chinese translation; en = English translation.',
  'cue (English, one sentence) = a small visible in-place action by the partner at that moment, e.g. "The barista nods and gestures toward the menu board." The partner stays in the same place; use hand gestures, nods and expressions only. Never move or describe the camera, never add people, never show text, never mention the learner\'s body.',
  'scenePrompt (English, 1-3 sentences) describes WHO and WHERE for the first video frame: the partner\'s role and plain clothing in the setting, natural lighting, static eye-level first-person view. audioPrompt (English, one sentence) = environmental ambience only: no voices, speech, singing or music.',
  'When hasPhotoBackdrop is true, the learner\'s own photo is the first video frame and you cannot see it: scenePrompt must NOT describe the room, furniture, layout, colours or lighting. Describe only the partner (role, plain clothing, natural posture) standing or sitting in front of the viewer "in this existing space", and say the surroundings stay exactly as they are. Cues must not name furniture or objects that may not exist in the photo; prefer gestures, nods and expressions.',
  'vocab: 2 to 5 useful words per beat that appear in the conversation; objectHint = a lowercase English noun for a visible object (or the closest one).',
  'title = a short Japanese title, then " — ", then an English title.',
  'Never use brands, companies, celebrities, real people, copyrighted characters or franchises, even if the learner asks; use generic equivalents. Do not invent prices or medical/legal facts as true; the learner may ask about them instead. If the request is not a place or situation for a conversation, write the closest sensible everyday scenario.',
].join('\n');

const JAPANESE = /[\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Han}]/u;
const KANJI = /\p{Script=Han}/u;
const LATIN = /^[\p{Script=Latin}\p{N}\p{P}\p{Zs}\p{Sc}'’~ー-]+$/u;
const clean = (s) => typeof s === 'string' ? s.replace(/[\u0000-\u001f]/g, ' ').replace(/\s+/g, ' ').trim() : '';
function fail(message) { throw new Error(message); }
function checkUtterance(u, where) {
  const out = Object.fromEntries(Object.keys(utterance).map((k) => [k, clean(u?.[k])]));
  if (Object.values(out).some((v) => !v || v.length > 300)) fail(`${where}: missing or oversized field`);
  if (!JAPANESE.test(out.jp)) fail(`${where}: jp is not Japanese`);
  if (KANJI.test(out.kana) || !JAPANESE.test(out.kana) || /[a-z]/i.test(out.kana)) fail(`${where}: kana must be a kanji-free reading`);
  if (!LATIN.test(out.romaji)) fail(`${where}: romaji must be Latin script`);
  return out;
}

/** Validate model output and convert it to a lesson. Throws Error (retryable) on bad content. */
export function scenarioToLesson(candidate, { id, level, request, requestLanguage, startImage = '', createdAt }) {
  const beats = Array.isArray(candidate?.beats) ? candidate.beats : fail('no beats');
  const total = beats.reduce((n, b) => n + (Array.isArray(b?.lines) ? b.lines.length : 0), 0);
  if (beats.length < 1 || beats.length > 5 || total < 3 || total > 12) fail('scenario must have 1-5 beats and 3-12 lines');
  const partnerRole = { ja: clean(candidate.partnerRole?.ja), en: clean(candidate.partnerRole?.en) };
  if (!partnerRole.ja || !partnerRole.en || partnerRole.ja.length > 40 || partnerRole.en.length > 60) fail('invalid partner role');
  let index = 0;
  const lesson = {
    id, level,
    title: clean(candidate.title).slice(0, 160) || fail('missing title'),
    scenePrompt: safePrompt(candidate.scenePrompt, '') || fail('invalid scene prompt'),
    audioPrompt: safePrompt(candidate.audioPrompt, '') || fail('invalid audio prompt'),
    startImage,
    partnerRole,
    beats: beats.map((beat, b) => ({
      goal: clean(beat.goal).slice(0, 300) || fail(`beat ${b + 1}: missing goal`),
      lines: beat.lines.map((line) => {
        const where = `line ${++index}`;
        const opener = index === 1 && Object.keys(utterance).every((k) => !clean(line.partner?.[k]));
        const cue = safePrompt(line.cue, '') || fail(`${where}: invalid cue`);
        const alternatives = (Array.isArray(line.alternatives) ? line.alternatives : []).slice(0, 3).map((alt, a) => checkUtterance(alt, `${where} alternative ${a + 1}`));
        return { ...checkUtterance(line, where), cue, partner: opener ? null : checkUtterance(line.partner, `${where} partner`), alternatives };
      }),
      vocab: (Array.isArray(beat.vocab) ? beat.vocab : []).slice(0, 6).map((v) => ({ jp: clean(v.jp), kana: clean(v.kana), en: clean(v.en), objectHint: clean(v.objectHint).toLowerCase().replace(/[^a-z0-9_ -]/g, '').slice(0, 40) }))
        .filter((v) => v.jp && v.kana && v.en && /^[a-z]/.test(v.objectHint)),
    })),
    scenario: { request, requestLanguage, level, createdAt, generated: true },
  };
  if (hasBannedTerm(JSON.stringify(lesson))) fail('scenario contains a blocked character or brand');
  return validateLesson(lesson);
}

export function createScenarioGenerator({ apiKey = process.env.OPENAI_API_KEY, model = process.env.OPENAI_MODEL || DEFAULT_OPENAI_MODEL, reasoningEffort = process.env.OPENAI_REASONING_EFFORT || 'none', fetchImpl = fetch, timeoutMs = 60_000, now = () => new Date() } = {}) {
  const key = apiKey?.trim();
  const luna = model === 'gpt-6-luna' || model.startsWith('gpt-6-luna-');
  async function call(input) {
    const response = await fetchImpl('https://api.openai.com/v1/responses', {
      method: 'POST', signal: AbortSignal.timeout(timeoutMs),
      headers: { 'content-type': 'application/json', authorization: `Bearer ${key}` },
      body: JSON.stringify({
        model, ...(luna ? { reasoning: { effort: reasoningEffort } } : {}), store: false, max_output_tokens: 8000,
        instructions: SCENARIO_INSTRUCTIONS, input: JSON.stringify(input),
        text: { format: { type: 'json_schema', name: 'conversation_scenario', strict: true, schema: strictOutputSchema(SCENARIO_SCHEMA) } },
      }),
    });
    // Provider bodies can echo request data; never surface them.
    if (response.status === 429) throw new AgentError(429, 'The scenario writer is busy. Try again in a moment.');
    if (!response.ok) throw new AgentError(502, 'The scenario writer is unavailable right now.');
    const result = await response.json();
    if (result.status !== 'completed' || result.error) throw new Error('incomplete model response');
    const content = (result.output || []).filter((item) => item.type === 'message').flatMap((item) => item.content || []);
    if (content.some((item) => item.type === 'refusal')) throw new AgentError(422, 'That request cannot be turned into a practice scenario. Try describing an everyday situation.');
    const raw = content.filter((item) => item.type === 'output_text').map((item) => item.text).join('');
    if (!raw || raw.length > 60_000) throw new Error('invalid model output');
    return JSON.parse(raw);
  }
  return {
    configured: Boolean(key),
    /** One automatic retry, with the validation problem, when the content check fails. */
    async generate({ request, level, requestLanguage = '', startImage = '' }) {
      if (!key) throw new AgentError(503, 'Scenario generation needs OPENAI_API_KEY in portal/.env');
      const id = `gen-${randomBytes(6).toString('hex')}`;
      const base = { request, level, hasPhotoBackdrop: Boolean(startImage) };
      let problem = null;
      for (let attempt = 0; attempt < 2; attempt++) {
        let candidate;
        try { candidate = await call(problem ? { ...base, previousAttemptRejected: problem } : base); }
        catch (e) { if (e instanceof AgentError) throw e; problem = 'The previous answer was not valid JSON for the schema.'; console.warn(`[scenarios] attempt ${attempt + 1} rejected: ${e.message}`); continue; }
        try { return scenarioToLesson(candidate, { id, level, request, requestLanguage, startImage, createdAt: now().toISOString() }); }
        catch (e) { if (e instanceof AgentError) throw new AgentError(422, 'The generated scenario failed a safety check. Try rephrasing your request.'); problem = `Fix this: ${e.message}.`; console.warn(`[scenarios] attempt ${attempt + 1} rejected: ${e.message}`); }
      }
      throw new AgentError(502, 'Could not write a valid scenario. Please try again.');
    },
  };
}
