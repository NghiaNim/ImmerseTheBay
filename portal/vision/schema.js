export const MAX_FRAME_BYTES = 2 * 1024 * 1024;
export const MIN_CONFIDENCE = 0.8;
export const POINT_TOLERANCE = 0.025;

// Only these ordinary objects can reach scene steering. Model labels, OCR,
// packaging text and instructions in the frame never become prompt text.
export const OBJECTS = Object.freeze({
  bottle: { label: 'ボトル', noun: 'bottle' },
  sandwich: { label: 'サンドイッチ', noun: 'sandwich' },
  rice_ball: { label: 'おにぎり', noun: 'rice ball' },
  cup: { label: 'カップ', noun: 'cup' },
  pitcher: { label: 'ミルクピッチャー', noun: 'milk pitcher' },
  bag: { label: '袋', noun: 'bag' },
  basket: { label: 'かご', noun: 'shopping basket' },
  counter: { label: 'カウンター', noun: 'checkout counter' },
  door: { label: 'ドア', noun: 'door' },
  shelf: { label: '棚', noun: 'shelf' },
  receipt: { label: 'レシート', noun: 'receipt' },
  payment_terminal: { label: '決済端末', noun: 'payment terminal' },
});

const unit = { type: 'number', minimum: 0, maximum: 1 };
export const VISION_SCHEMA = {
  type: 'object', additionalProperties: false,
  required: ['object', 'confidence', 'bounds'],
  properties: {
    object: { type: ['string', 'null'], enum: [...Object.keys(OBJECTS), null] },
    confidence: unit,
    bounds: { anyOf: [
      { type: 'null' },
      { type: 'object', additionalProperties: false, required: ['x', 'y', 'width', 'height'],
        properties: { x: unit, y: unit, width: unit, height: unit } },
    ] },
  },
};

export function noHit() { return { object: null, label: null, confidence: 0, bounds: null, intent: 'none' }; }
export class VisionError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}
const plain = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const normalized = (n) => typeof n === 'number' && Number.isFinite(n) && n >= 0 && n <= 1;
const exactKeys = (v, keys) => plain(v) && Object.keys(v).length === keys.length && keys.every((k) => Object.hasOwn(v, k));

// Parse only JPEG markers to bound pixel dimensions before sending the frame.
// This is not a decoder; the provider still validates the compressed image.
function jpegDimensions(bytes) {
  if (bytes.length < 12 || bytes[0] !== 0xff || bytes[1] !== 0xd8
      || bytes[bytes.length - 2] !== 0xff || bytes[bytes.length - 1] !== 0xd9) return null;
  const sof = new Set([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf]);
  let i = 2;
  while (i + 3 < bytes.length) {
    if (bytes[i++] !== 0xff) return null;
    while (bytes[i] === 0xff) i++;
    const marker = bytes[i++];
    if (marker === 0xda || marker === 0xd9) return null;
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) continue;
    if (i + 2 > bytes.length) return null;
    const length = bytes.readUInt16BE(i);
    if (length < 2 || i + length > bytes.length) return null;
    if (sof.has(marker)) {
      if (length < 8) return null;
      return { height: bytes.readUInt16BE(i + 3), width: bytes.readUInt16BE(i + 5) };
    }
    i += length;
  }
  return null;
}

export function validateJpegFrame(frame) {
  const prefix = 'data:image/jpeg;base64,';
  if (typeof frame !== 'string' || !frame.startsWith(prefix)) throw new VisionError(400, 'frame must be a base64 JPEG data URL');
  if (frame.length > prefix.length + 4 * Math.ceil(MAX_FRAME_BYTES / 3)) throw new VisionError(413, 'JPEG frame exceeds 2 MiB');
  const encoded = frame.slice(prefix.length);
  if (!encoded || encoded.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(encoded)) throw new VisionError(400, 'Invalid JPEG encoding');
  const bytes = Buffer.from(encoded, 'base64');
  if (bytes.length > MAX_FRAME_BYTES) throw new VisionError(413, 'JPEG frame exceeds 2 MiB');
  if (bytes.toString('base64') !== encoded) throw new VisionError(400, 'Invalid JPEG encoding');
  const size = jpegDimensions(bytes);
  if (!size || !size.width || !size.height || size.width > 4096 || size.height > 4096 || size.width * size.height > 8_000_000) {
    throw new VisionError(400, 'Invalid JPEG or image dimensions exceed the prototype limit');
  }
  return size;
}

export function validateRequest(body) {
  if (!exactKeys(body, ['frame', 'point', 'gesture']) || !exactKeys(body.point, ['x', 'y'])
      || !normalized(body.point.x) || !normalized(body.point.y) || !['point', 'pinch'].includes(body.gesture)) {
    throw new VisionError(400, 'Expected a JPEG frame, normalized point {x,y}, and point or pinch gesture');
  }
  validateJpegFrame(body.frame);
  return { frame: body.frame, point: { x: body.point.x, y: body.point.y }, gesture: body.gesture };
}

export function gateDetection(candidate, { point, gesture }) {
  if (!plain(point) || !normalized(point.x) || !normalized(point.y) || !['point', 'pinch'].includes(gesture)
      || !exactKeys(candidate, ['object', 'confidence', 'bounds']) || typeof candidate.object !== 'string' || !Object.hasOwn(OBJECTS, candidate.object)
      || !normalized(candidate.confidence) || candidate.confidence < MIN_CONFIDENCE
      || !exactKeys(candidate.bounds, ['x', 'y', 'width', 'height'])) return noHit();
  const b = candidate.bounds;
  if (!Object.values(b).every(normalized) || b.width <= 0 || b.height <= 0
      || b.x + b.width > 1.000001 || b.y + b.height > 1.000001
      || b.width * b.height > 0.65 || b.width * b.height < 0.0001) return noHit();
  const dx = Math.max(b.x - point.x, 0, point.x - b.x - b.width);
  const dy = Math.max(b.y - point.y, 0, point.y - b.y - b.height);
  if (Math.hypot(dx, dy) > POINT_TOLERANCE) return noHit();
  return { object: candidate.object, label: OBJECTS[candidate.object].label, confidence: candidate.confidence,
    bounds: { x: b.x, y: b.y, width: b.width, height: b.height }, intent: gesture === 'pinch' ? 'select' : 'inspect' };
}

/** Use only the server-gated result; never interpolate a model-generated label. */
export function createVisionPrompt(result, point) {
  if (!result || !['inspect', 'select'].includes(result.intent)) return null;
  const hit = gateDetection({ object: result.object, confidence: result.confidence, bounds: result.bounds },
    { point, gesture: result.intent === 'select' ? 'pinch' : 'point' });
  if (!hit.object) return null;
  const noun = OBJECTS[hit.object].noun;
  return hit.intent === 'select'
    ? `The shop assistant acknowledges the selected ${noun} with a small gesture toward it.`
    : `The shop assistant points toward the ${noun} the visitor is looking at.`;
}
