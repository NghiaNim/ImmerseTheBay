import { hasBannedTerm } from './guardrails.js';

// A steering constraint, not a claim that a face was visible or verified.
export const CHARACTER_ANCHOR = 'Preserve each established person from the original starting reference: exact facial structure, eyes, nose and mouth, same age, skin tone, hair, clothing and accessories.';
export const SCENE_ANCHOR = 'Maintain the original first-person viewpoint, scene and lighting; no cuts, added people or recasting; natural conversational gestures without generated speech.';
const NEUTRAL_ACTION = 'The scene continues with natural expressions and movement';
const CHARACTER = '(?:clerk|assistant|host|person|character|face|facial features|identity|hairstyle|hair|skin tone|clothing|outfit|accessories)';
const conflicts = [
  new RegExp(`\\b(?:replace|swap|recast|change|alter|transform|redesign)\\b[^.!?。！？]{0,65}\\b${CHARACTER}\\b`, 'iu'),
  new RegExp(`\\b(?:new|different|another)\\s+${CHARACTER}\\b`, 'iu'),
  /\b(?:face[- ]?swap|age[- ]?shift|de[- ]?age|younger|older|rejuvenat\w*)\b/iu,
  /\b(?:cut to|teleport|scene reset|restart the scene|another store|different store)\b/iu,
  /(?:换脸|更换店员|另一个店员|改变肤色|变年轻|变老|別の店員|顔を変)/u,
];
function usable(value) {
  if (typeof value !== 'string' || hasBannedTerm(value)) return '';
  const clean = value.normalize('NFKC').replace(/[\u0000-\u001f\p{Cf}]/gu, ' ').replace(/\s+/gu, ' ').trim();
  if (!clean || conflicts.some((pattern) => pattern.test(clean))) return '';
  return clean;
}

/** Compose after model refinement, for visual prompts only. Audio is untouched.
 * Explicit conflicts fall back to an authored action. This small guard is not a
 * semantic classifier; visual continuity must still be checked on actual frames.
 */
export function composeVisualPrompt(action, fallback = NEUTRAL_ACTION) {
  // Strip our own anchors to make composition idempotent.
  const strip = (value) => typeof value === 'string'
    ? value.replace(CHARACTER_ANCHOR, '').replace(SCENE_ANCHOR, '').trim() : value;
  const selected = usable(strip(action)) || usable(strip(fallback)) || NEUTRAL_ACTION;
  const first = (selected.match(/^[^.!?。！？]+/u)?.[0] || NEUTRAL_ACTION).trim();
  const overhead = `${CHARACTER_ANCHOR} . ${SCENE_ANCHOR}`;
  const wordsAvailable = 95 - overhead.split(/\s+/u).length + 1; // the placeholder period is not an action word
  const charsAvailable = 600 - overhead.length;
  let delta = first.split(/\s+/u).slice(0, wordsAvailable).join(' ');
  if (delta.length > charsAvailable) {
    delta = delta.slice(0, charsAvailable);
    // Prefer a complete word, retaining a bounded CJK/no-space action as well.
    const boundary = delta.lastIndexOf(' ');
    if (boundary > charsAvailable / 2) delta = delta.slice(0, boundary);
    if (/[\uD800-\uDBFF]$/u.test(delta)) delta = delta.slice(0, -1);
  }
  delta = delta.trim() || NEUTRAL_ACTION;
  return `${CHARACTER_ANCHOR} ${delta}. ${SCENE_ANCHOR}`;
}


export const AMBIENT_ANCHOR = 'Environmental sounds only; no speech, dialogue, narration, voices, singing or music.';
const NEUTRAL_AMBIENCE = 'Soft room ambience with gentle ventilation';
const VOCAL_OR_MUSIC = /\b(?:speak\w*|speech|spoken|say|says|saying|said|tell|tells|telling|talk\w*|chat\w*|convers\w*|dialog\w*|narrat\w*|voice\w*|vocal\w*|sing\w*|sang|sung|song\w*|lyrics?|music\w*|melody|melodies|announce\w*|greet\w*|welcom\w*|reply|replies|answer\w*|pronounc\w*|read(?:s|ing|out)?|whisper\w*|murmur\w*|utter\w*|laugh\w*|giggl\w*|chuckl\w*|scream\w*|shout\w*|hum(?:s|ming|med)?|radio|television|tv|podcast)\b|(?:说话|說話|人声|人聲|语音|語音|对话|對話|旁白|聊天|唱歌|歌声|歌聲|音乐|音樂|广播|廣播|会話|会话|話し声|話す|声で|ナレーション|歌う|音楽|いらっしゃいませ|欢迎|歡迎|こんにちは|ありがとうございます)/iu;

/** Apply to every outgoing set_audio_prompt after refinement. The fixed negative
 * constraint survives truncation; matching a prohibited cue discards the whole
 * candidate rather than leaving a partial spoken instruction behind.
 */
export function composeAmbientPrompt(value) {
  const raw = typeof value === 'string' ? value.replace(AMBIENT_ANCHOR, '') : '';
  const clean = raw.normalize('NFKC').replace(/[\u0000-\u001f\p{Cf}]/gu, ' ').replace(/\s+/gu, ' ').trim();
  const safe = clean && !hasBannedTerm(clean) && !VOCAL_OR_MUSIC.test(clean) ? clean : NEUTRAL_AMBIENCE;
  let delta = (safe.match(/^[^.!?。！？]+/u)?.[0] || NEUTRAL_AMBIENCE).trim();
  const overhead = `${AMBIENT_ANCHOR} .`;
  const charsAvailable = 600 - overhead.length;
  const wordsAvailable = 95 - AMBIENT_ANCHOR.split(/\s+/u).length;
  delta = delta.split(/\s+/u).slice(0, wordsAvailable).join(' ');
  if (delta.length > charsAvailable) {
    delta = delta.slice(0, charsAvailable);
    const boundary = delta.lastIndexOf(' ');
    if (boundary > charsAvailable / 2) delta = delta.slice(0, boundary);
    if (/[\uD800-\uDBFF]$/u.test(delta)) delta = delta.slice(0, -1);
  }
  return `${AMBIENT_ANCHOR} ${delta.trim() || NEUTRAL_AMBIENCE}.`;
}
