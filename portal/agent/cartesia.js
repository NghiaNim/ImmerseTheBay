import { AgentError } from './lessons.js';

export const MAX_SPEECH_BYTES = 8 * 1024 * 1024;
export const DEFAULT_CARTESIA_MODEL = 'sonic-latest';
export const DEFAULT_CARTESIA_VERSION = '2026-08-14';
const cancelBody = (body) => { try { Promise.resolve(body?.cancel()).catch(() => {}); } catch {} };

// Cartesia streams WAV with unknown RIFF/data sizes. This endpoint buffers
// the complete response, so fill in the lengths expected by file decoders.
function finalizeWav(audio) {
  if (audio.readUInt32LE(4) !== 0xffffffff) return audio;
  audio.writeUInt32LE(audio.length - 8, 4);
  for (let offset = 12; offset + 8 <= audio.length;) {
    const size = audio.readUInt32LE(offset + 4);
    if (audio.toString('ascii', offset, offset + 4) === 'data' && size === 0xffffffff) {
      audio.writeUInt32LE(audio.length - offset - 8, offset + 4);
      break;
    }
    offset += 8 + size + (size & 1);
  }
  return audio;
}

// Give the headset decoder interleaved left/right samples explicitly.
export function stereoSpeechWav(audio) {
  const invalidAudio = () => new AgentError(502, 'Invalid speech audio');
  if (!Buffer.isBuffer(audio) || audio.length < 44 ||
      audio.toString('latin1', 0, 4) !== 'RIFF' ||
      audio.toString('latin1', 8, 12) !== 'WAVE' ||
      audio.readUInt32LE(4) !== audio.length - 8) throw invalidAudio();
  if (audio.length > MAX_SPEECH_BYTES) throw new AgentError(502, 'Speech response exceeds size limit');

  let format, samples;
  for (let offset = 12; offset < audio.length;) {
    const start = offset + 8;
    if (start > audio.length) throw invalidAudio();
    const kind = audio.toString('latin1', offset, offset + 4);
    const length = audio.readUInt32LE(offset + 4);
    const end = start + length;
    offset = end + (length & 1);
    if (offset > audio.length) throw invalidAudio();
    if (kind === 'fmt ') {
      if (format || length < 16) throw invalidAudio();
      format = audio.subarray(start, end);
    } else if (kind === 'data') {
      if (samples) throw invalidAudio();
      samples = audio.subarray(start, end);
    }
  }
  if (!format || !samples?.length) throw invalidAudio();
  const channels = format.readUInt16LE(2);
  const rate = format.readUInt32LE(4);
  const frameSize = channels * 2;
  if (format.readUInt16LE(0) !== 1 || ![1, 2].includes(channels) || format.readUInt16LE(14) !== 16 ||
      rate < 1 || rate > Math.floor(0xffffffff / 4) || format.readUInt32LE(8) !== rate * frameSize ||
      format.readUInt16LE(12) !== frameSize || samples.length % frameSize !== 0) throw invalidAudio();
  if (channels === 2) return audio;

  const stereoBytes = samples.length * 2;
  if (44 + stereoBytes > MAX_SPEECH_BYTES) throw new AgentError(502, 'Speech response exceeds size limit');
  const stereo = Buffer.alloc(44 + stereoBytes);
  stereo.write('RIFF', 0);
  stereo.writeUInt32LE(stereo.length - 8, 4);
  stereo.write('WAVEfmt ', 8);
  stereo.writeUInt32LE(16, 16);
  stereo.writeUInt16LE(1, 20);
  stereo.writeUInt16LE(2, 22);
  stereo.writeUInt32LE(rate, 24);
  stereo.writeUInt32LE(rate * 4, 28);
  stereo.writeUInt16LE(4, 32);
  stereo.writeUInt16LE(16, 34);
  stereo.write('data', 36);
  stereo.writeUInt32LE(stereoBytes, 40);
  for (let input = 0, output = 44; input < samples.length; input += 2, output += 4) {
    const sample = samples.readUInt16LE(input);
    stereo.writeUInt16LE(sample, output);
    stereo.writeUInt16LE(sample, output + 2);
  }
  return stereo;
}

const PRONUNCIATION_SAMPLE_RATE = 44100;

function pronunciationFromEvents(bytes, text) {
  const chunks = [];
  const ranges = [];
  let done = false, aligned = true, cursor = 0, previousEnd = 0, length = 0;
  for (const event of bytes.toString('utf8').split(/\r?\n\r?\n/)) {
    const data = event.split(/\r?\n/).filter(line => line.startsWith('data:')).map(line => line.slice(5).trimStart()).join('\n');
    if (!data) continue;
    const payload = JSON.parse(data);
    if (payload.type === 'error' || (payload.status_code ?? 0) >= 400)
      throw new AgentError(502, 'Pronunciation service unavailable');
    if (payload.type === 'done') { done = true; break; }
    if (payload.type === 'chunk') {
      if (typeof payload.data !== 'string') throw new AgentError(502, 'Invalid pronunciation audio');
      const part = Buffer.from(payload.data, 'base64');
      length += part.length;
      if (length + 44 > MAX_SPEECH_BYTES) throw new AgentError(502, 'Speech response exceeds size limit');
      chunks.push(part);
    }
    if (payload.type !== 'timestamps' || !aligned) continue;
    const { words, start, end } = payload.word_timestamps || {};
    if (!Array.isArray(words) || !Array.isArray(start) || !Array.isArray(end) || words.length !== start.length || words.length !== end.length) {
      aligned = false;
      continue;
    }
    for (let i = 0; i < words.length; ++i) {
      const word = words[i];
      while (/\s/u.test(text[cursor] || '')) ++cursor;
      const from = Math.round(start[i] * 1000), to = Math.round(end[i] * 1000);
      if (typeof word !== 'string' || !word.length || !text.startsWith(word, cursor) ||
          !Number.isFinite(start[i]) || !Number.isFinite(end[i]) || from < previousEnd || to < from) {
        aligned = false;
        break;
      }
      if (to > from) ranges.push({ start: cursor, end: cursor + word.length, from, to });
      previousEnd = to;
      cursor += word.length;
    }
  }
  if (!done || length < 2 || length % 2) throw new AgentError(502, 'Incomplete pronunciation audio');
  const audio = Buffer.alloc(44 + length);
  audio.write('RIFF', 0);
  audio.writeUInt32LE(audio.length - 8, 4);
  audio.write('WAVEfmt ', 8);
  audio.writeUInt32LE(16, 16);
  audio.writeUInt16LE(1, 20);
  audio.writeUInt16LE(1, 22);
  audio.writeUInt32LE(PRONUNCIATION_SAMPLE_RATE, 24);
  audio.writeUInt32LE(PRONUNCIATION_SAMPLE_RATE * 2, 28);
  audio.writeUInt16LE(2, 32);
  audio.writeUInt16LE(16, 34);
  audio.write('data', 36);
  audio.writeUInt32LE(length, 40);
  let offset = 44;
  for (const chunk of chunks) { chunk.copy(audio, offset); offset += chunk.length; }
  const duration = length * 1000 / (PRONUNCIATION_SAMPLE_RATE * 2);
  const hasValidTimings = aligned && !text.slice(cursor).trim() && ranges.every(range => range.to <= duration + 1);
  return { audio, ranges: hasValidTimings ? ranges : [] };
}

// One configured service instance has two slots; no unbounded text/audio queue.
// Audio, transcript and provider response bodies are never logged or persisted.
export function createCartesiaSpeech({ apiKey = process.env.CARTESIA_API_KEY,
  voiceId = process.env.CARTESIA_VOICE_ID,
  femaleVoiceId = process.env.CARTESIA_VOICE_ID_FEMALE,
  maleVoiceId = process.env.CARTESIA_VOICE_ID_MALE,
  model = process.env.CARTESIA_MODEL || DEFAULT_CARTESIA_MODEL,
  apiVersion = process.env.CARTESIA_API_VERSION || DEFAULT_CARTESIA_VERSION,
  fetchImpl = fetch, timeoutMs = 15_000 } = {}) {
  const female = femaleVoiceId?.trim();
  const male = maleVoiceId?.trim();
  const fallback = voiceId?.trim() || female || male;
  let active = 0;
  // partnerVoice is a logical style supplied by trusted lesson state. The
  // public speech route only forwards text and language.
  return async function synthesize({ text, language = 'ja', signal, partnerVoice, timed = false, slow = false } = {}) {
    if (typeof text !== 'string' || !text.trim() || text.length > 1000) throw new AgentError(400, 'Speech text must be 1 to 1000 characters');
    if (timed && (language !== 'ja' || text.length > 120 || typeof slow !== 'boolean'))
      throw new AgentError(400, 'Invalid pronunciation request');
    if (!['ja', 'en', 'zh'].includes(language)) throw new AgentError(400, 'Unsupported speech language');
    if (signal !== undefined && !(signal instanceof AbortSignal)) throw new AgentError(400, 'Invalid speech cancellation signal');
    if (partnerVoice != null && partnerVoice !== 'female' && partnerVoice !== 'male')
      throw new AgentError(400, 'Invalid partner voice');
    const selectedVoiceId = partnerVoice === 'female' ? female : partnerVoice === 'male' ? male : fallback;
    if (!apiKey?.trim() || !selectedVoiceId) {
      throw new AgentError(503, partnerVoice == null
        ? 'Server speech requires CARTESIA_API_KEY and CARTESIA_VOICE_ID'
        : 'The selected speech voice is not configured');
    }
    if (signal?.aborted) throw new AgentError(499, 'Speech request canceled');
    if (active >= 2) throw new AgentError(429, 'Speech is busy; try again shortly');
    active++;
    const controller = new AbortController();
    let timedOut = false;
    const onAbort = () => controller.abort();
    signal?.addEventListener('abort', onAbort, { once: true });
    const timer = setTimeout(() => { timedOut = true; controller.abort(); }, timeoutMs);
    let reader;
    let response;
    let rejectAbort;
    const aborted = new Promise((_, reject) => { rejectAbort = reject; });
    const abortWork = () => {
      // Reject before canceling: cancel() can resolve a pending read as done.
      rejectAbort(new AgentError(timedOut ? 504 : 499, timedOut ? 'Speech synthesis timed out' : 'Speech request canceled'));
      if (reader) cancelBody(reader);
      else cancelBody(response?.body);
    };
    controller.signal.addEventListener('abort', abortWork, { once: true });
    const wait = (promise) => Promise.race([promise, aborted]);
    try {
      const maxBodyBytes = timed ? MAX_SPEECH_BYTES * 2 : MAX_SPEECH_BYTES;
      response = await wait(fetchImpl(timed ? 'https://api.cartesia.ai/tts/sse' : 'https://api.cartesia.ai/tts/bytes', {
        method: 'POST', signal: controller.signal,
        headers: { Authorization: `Bearer ${apiKey.trim()}`, 'Cartesia-Version': apiVersion, 'Content-Type': 'application/json' },
        body: JSON.stringify({ model_id: model, transcript: text.trim(), voice: selectedVoiceId,
          output_format: { container: timed ? 'raw' : 'wav', encoding: 'pcm_s16le', sample_rate: PRONUNCIATION_SAMPLE_RATE }, language,
          ...(timed ? { add_timestamps: true, use_normalized_timestamps: false, ...(slow ? { generation_config: { speed: 0.8 } } : {}) } : {}) }),
      }));
      if (!response.ok) {
        cancelBody(response.body);
        if (response.status === 429) throw new AgentError(429, 'Speech is busy; try again shortly');
        throw new AgentError(response.status === 503 ? 503 : 502, 'Speech service unavailable');
      }
      const declared = Number(response.headers?.get('content-length'));
      if (Number.isFinite(declared) && declared > maxBodyBytes) {
        cancelBody(response.body);
        throw new AgentError(502, 'Speech response exceeds size limit');
      }
      if (!response.body?.getReader) throw new AgentError(502, 'Invalid speech response');
      reader = response.body.getReader();
      const chunks = [];
      let total = 0;
      while (true) {
        const { done, value } = await wait(reader.read());
        if (done) break;
        if (!(value instanceof Uint8Array)) throw new AgentError(502, 'Invalid speech response');
        total += value.byteLength;
        if (total > maxBodyBytes) throw new AgentError(502, 'Speech response exceeds size limit');
        chunks.push(Buffer.from(value));
      }
      if (controller.signal.aborted) throw new AgentError(timedOut ? 504 : 499, timedOut ? 'Speech synthesis timed out' : 'Speech request canceled');
      const audio = Buffer.concat(chunks, total);
      if (timed) return pronunciationFromEvents(audio, text.trim());
      if (audio.length < 12 || audio.toString('ascii', 0, 4) !== 'RIFF' || audio.toString('ascii', 8, 12) !== 'WAVE') throw new AgentError(502, 'Invalid speech audio');
      return finalizeWav(audio);
    } catch (error) {
      if (reader) cancelBody(reader);
      else cancelBody(response?.body);
      if (error instanceof AgentError) throw error;
      if (controller.signal.aborted) throw new AgentError(timedOut ? 504 : 499, timedOut ? 'Speech synthesis timed out' : 'Speech request canceled');
      throw new AgentError(502, 'Speech service unavailable');
    } finally {
      clearTimeout(timer);
      signal?.removeEventListener('abort', onAbort);
      controller.signal.removeEventListener('abort', abortWork);
      try { reader?.releaseLock(); } catch {}
      active--;
    }
  };
}
