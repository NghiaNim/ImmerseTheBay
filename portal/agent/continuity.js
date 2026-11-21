import { hasBannedTerm } from './guardrails.js';

// A steering constraint, not a claim that a face was visible or verified.
export const CHARACTER_ANCHOR = 'The established face, hair and clothing remain unchanged.';
export const SCENE_ANCHOR = 'Static eye-level medium-wide first-person POV, locked position, orientation and focal length; framing and background stay stationary, foreground clear.';
export const SPEAKING_MOTION = '; the established speaker moves their lips silently, with listening pauses';
const NEUTRAL_ACTION = 'The host stays in place with a natural expression';
const CHARACTER = '(?:clerk|assistant|host|person|character|face|facial features|identity|gender|sex|age|hairstyle|hair|skin tone|clothing|outfit|accessories)';
const conflicts = [
  new RegExp(`\\b(?:replace|swap|recast|change|alter|transform|redesign)\\b[^.!?。！？]{0,65}\\b${CHARACTER}\\b`, 'iu'),
  new RegExp(`\\b(?:new|different|another)\\s+${CHARACTER}\\b`, 'iu'),
  /\b(?:face[- ]?swap|age[- ]?shift|de[- ]?age|younger|older|rejuvenat\w*)\b/iu,
  /\b(?:cut to|teleport|scene reset|restart the scene|another store|different store)\b/iu,
  /(?:换脸|更换店员|另一个店员|改变肤色|变年轻|变老|別の店員|顔を変)/u,
  /\b(?:camera|viewpoint|view|shot|framing)\b[^.!?。！？]{0,70}\b(?:move[sd]?|moving|approach\w*|follow\w*|track\w*|pan\w*|tilt\w*|zoom\w*|dolly|dollies|orbit\w*|rotate\w*|closer|close-up|reframe\w*|cut)\b/iu,
  /\b(?:zoom\w*|dolly|dollies|close[- ]?up|over[- ]the[- ]shoulder|over[- ]shoulder|third[- ]person)\b/iu,
  /\b(?:pan|tilt|rotate|move|reframe|orbit)\b[^.!?。！？]{0,35}\b(?:camera|viewpoint|view|shot)\b/iu,
  /\b(?:visitor|learner|customer|shopper)\s+(?:(?:gently|slowly|then|now)\s+)*(?:places?|puts?|sets?|takes?|picks?|reaches?|walks?|moves?|enters?|appears?|stands?|sits?|holds?|turns?|faces?|looks?|steps?|is visible)\b/iu,
  /\b(?:new|another|additional|extra)\s+(?:person|customer|shopper|visitor|clerk|assistant)\b|\b(?:person|customer|shopper|visitor)\s+enters?\b/iu,
  /\b(?:clerk|assistant|host)\b[^.!?。！？]{0,40}\b(?:walks?|leaves?|approaches?|steps?|moves closer|leans? (?:closer|toward|towards)|fetches?)\b/iu,
  /(?:镜头|鏡頭|カメラ)[^。！？.!?]{0,20}(?:移动|移動|推进|推進|拉近|转向|轉向|ズーム|近づ)|(?:特写|特寫|第三人称|第三人稱|肩越し)/u,
];
function usable(value) {
  if (typeof value !== 'string' || hasBannedTerm(value)) return '';
  const clean = value.normalize('NFKC').replace(/[\u0000-\u001f\p{Cf}]/gu, ' ').replace(/\s+/gu, ' ').trim();
  if (!clean || conflicts.some((pattern) => pattern.test(clean))) return '';
  // The learner is the viewpoint, not another visible character. Translate
  // recipient references in authored cues into fixed screen-space directions.
  return clean
    .replace(/\blooks? toward(?:s)? the (?:learner|visitor|customer)\b/giu, 'looks straight ahead')
    .replace(/\btoward(?:s)? the (?:learner|visitor|customer)\b/giu, 'toward the near counter edge')
    .replace(/\bcloser to the (?:learner|visitor|customer)\b/giu, 'slightly toward the near counter edge');
}

/** Each turn carries one physical delta plus the server-owned identity lock.
 * Initial WHO/WHERE may also use a trusted reference description. The identity
 * lock persists through continuation and rejoin, including short action cues.
 */
export function composeVisualPrompt(action, fallback = NEUTRAL_ACTION, { initial = false, subject = '', speaking = false, visiblePartner = true, identityAnchor = CHARACTER_ANCHOR, environmentalMotion = false } = {}) {
  // Without a photographed partner, dialogue may branch off-camera but visual
  // actions must not synthesize a character (including unsafe authored fallbacks).
  // Keep the reference environment itself authoritative instead of repeating
  // human nouns from an LLM cue or a default store script.
  if (visiblePartner === false) {
    const motion = environmentalMotion ? usable(action).match(/^[^.!?。！？]+/u)?.[0] || '' : '';
    const grounded = motion && /\b(?:leaves|branches|curtain|reflections?|water|steam|light|shadows?|rain|clouds?|fountain|fan)\b/iu.test(motion) &&
      !/\b(?:people|person|person's|body|figure|faces?|clerk|barista|host|partne?r|man|woman|hands?|customer|walks?|enters?|appears?|moves toward|new|forms?)\b/iu.test(motion);
    return `The photographed environment retains its original layout and lighting. ${grounded ? motion.slice(0, 200) + '.' : 'Existing objects remain stationary.'} ${SCENE_ANCHOR}`;
  }
  const identity = usable(identityAnchor);
  const anchor = identity && identity.length <= 240 ? identity : CHARACTER_ANCHOR;
  const locked = anchor !== CHARACTER_ANCHOR;
  const strip = (value) => typeof value === 'string'
    ? value.replaceAll(anchor, '').replaceAll(CHARACTER_ANCHOR, '').replaceAll(SCENE_ANCHOR, '').replaceAll(SPEAKING_MOTION, '').replace(/^[.;\s]+/u, '').trim() : value;
  // With a pinned cast, action deltas cannot redefine gender or appearance.
  // The model supplies movement, while the trusted anchor supplies appearance.
  const deltaOnly = (value) => {
    const clean = usable(strip(value));
    return locked && /\b(?:male|female|man|woman|boy|girl|gender|beard|moustache|mustache|blond|blonde|redhead|hair|hairstyle|eyes|face|shirt|apron)\b|男性|女性|男店员|女店员|性别|性別/iu.test(clean) ? '' : clean;
  };
  const selected = deltaOnly(action) || deltaOnly(fallback) || NEUTRAL_ACTION;
  const first = (selected.match(/^[^.!?。！？]+/u)?.[0] || NEUTRAL_ACTION).trim();
  // subject is server-owned reference metadata, never the learner's caption.
  const description = initial && !locked ? (usable(subject).match(/^[^.!?。！？]+/u)?.[0] || '').slice(0, 180).trim() : '';
  const prefix = description ? `${description}; ` : '';
  const motion = speaking ? SPEAKING_MOTION : '';
  const overhead = `${prefix}${motion}. ${anchor} ${SCENE_ANCHOR}`;
  const wordsAvailable = 95 - overhead.split(/\s+/u).length + 1;
  const charsAvailable = 600 - overhead.length;
  let delta = first.split(/\s+/u).slice(0, wordsAvailable).join(' ');
  if (delta.length > charsAvailable) {
    delta = delta.slice(0, charsAvailable);
    const boundary = delta.lastIndexOf(' ');
    if (boundary > charsAvailable / 2) delta = delta.slice(0, boundary);
    if (/[\uD800-\uDBFF]$/u.test(delta)) delta = delta.slice(0, -1);
  }
  delta = delta.trim() || NEUTRAL_ACTION;
  return `${prefix}${delta}${motion}. ${anchor} ${SCENE_ANCHOR}`;
}


export const AMBIENT_ANCHOR = 'Environmental sounds only; no speech, dialogue, narration, voices, singing or music, including whispers, crowd chatter and distant announcements; every person is acoustically silent even when their lips move, with only non-vocal sounds from objects or the room.';
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
