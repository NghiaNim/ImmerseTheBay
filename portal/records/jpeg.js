// Minimal JPEG marker walk: validate structure, read dimensions and drop metadata
// segments (APP1 EXIF/XMP carries GPS; COM is free text). Pixel data is untouched.
export class PhotoError extends Error { constructor(status, message) { super(message); this.status = status; } }
const SOF = new Set([0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7, 0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf]);
const DROP = new Set([0xe1, 0xfe]);
export const MAX_PHOTO_SIDE = 4096;

export function sanitizeJpeg(input) {
  const buf = Buffer.from(input);
  if (buf.length < 4 || buf[0] !== 0xff || buf[1] !== 0xd8) throw new PhotoError(415, 'Photo must be a JPEG');
  const kept = [buf.subarray(0, 2)];
  let offset = 2, width = 0, height = 0;
  while (offset < buf.length) {
    if (buf[offset] !== 0xff) throw new PhotoError(400, 'Corrupt JPEG marker');
    const marker = buf[offset + 1];
    if (marker === 0xff) { offset++; continue; } // fill byte
    if (marker === 0xd8 || marker === undefined) throw new PhotoError(400, 'Corrupt JPEG marker');
    if (marker === 0xd9) { kept.push(buf.subarray(offset, offset + 2)); offset += 2; break; }
    if (marker === 0x01 || (marker >= 0xd0 && marker <= 0xd7)) { kept.push(buf.subarray(offset, offset + 2)); offset += 2; continue; }
    if (offset + 4 > buf.length) throw new PhotoError(400, 'Truncated JPEG');
    const length = buf.readUInt16BE(offset + 2);
    const end = offset + 2 + length;
    if (length < 2 || end > buf.length) throw new PhotoError(400, 'Truncated JPEG');
    if (SOF.has(marker)) {
      if (length < 7) throw new PhotoError(400, 'Corrupt JPEG header');
      height = buf.readUInt16BE(offset + 5); width = buf.readUInt16BE(offset + 7);
    }
    if (marker === 0xda) { kept.push(buf.subarray(offset)); offset = buf.length; break; } // scan data to the end
    if (!DROP.has(marker)) kept.push(buf.subarray(offset, end));
    offset = end;
  }
  if (!width || !height) throw new PhotoError(400, 'JPEG has no frame header');
  if (width > MAX_PHOTO_SIDE || height > MAX_PHOTO_SIDE) throw new PhotoError(413, `Photo exceeds ${MAX_PHOTO_SIDE} pixels per side`);
  return { jpeg: Buffer.concat(kept), width, height };
}

export function decodeDataUrl(value, label) {
  const match = typeof value === 'string' && /^data:image\/jpeg;base64,([A-Za-z0-9+/]+={0,2})$/.exec(value);
  if (!match) throw new PhotoError(400, `${label} must be a base64 JPEG data URL`);
  return Buffer.from(match[1], 'base64');
}
