import { AgentError } from './lessons.js';

export const MAX_SPEECH_BYTES = 8 * 1024 * 1024;
export const DEFAULT_CARTESIA_MODEL = 'sonic-latest';
export const DEFAULT_CARTESIA_VERSION = '2026-08-14';
const cancelBody = (body) => { try { Promise.resolve(body?.cancel()).catch(() => {}); } catch {} };

// One configured service instance has two slots; no unbounded text/audio queue.
// Audio, transcript and provider response bodies are never logged or persisted.
export function createCartesiaSpeech({ apiKey = process.env.CARTESIA_API_KEY,
  voiceId = process.env.CARTESIA_VOICE_ID,
  model = process.env.CARTESIA_MODEL || DEFAULT_CARTESIA_MODEL,
  apiVersion = process.env.CARTESIA_API_VERSION || DEFAULT_CARTESIA_VERSION,
  fetchImpl = fetch, timeoutMs = 15_000 } = {}) {
  let active = 0;
  return async function synthesize({ text, language = 'ja', signal } = {}) {
    if (typeof text !== 'string' || !text.trim() || text.length > 1000) throw new AgentError(400, 'Speech text must be 1 to 1000 characters');
    if (!['ja', 'en', 'zh'].includes(language)) throw new AgentError(400, 'Unsupported speech language');
    if (signal !== undefined && !(signal instanceof AbortSignal)) throw new AgentError(400, 'Invalid speech cancellation signal');
    if (!apiKey?.trim() || !voiceId?.trim()) throw new AgentError(503, 'Server speech requires CARTESIA_API_KEY and CARTESIA_VOICE_ID');
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
      response = await wait(fetchImpl('https://api.cartesia.ai/tts/bytes', {
        method: 'POST', signal: controller.signal,
        headers: { Authorization: `Bearer ${apiKey.trim()}`, 'Cartesia-Version': apiVersion, 'Content-Type': 'application/json' },
        body: JSON.stringify({ model_id: model, transcript: text.trim(), voice: voiceId.trim(),
          output_format: { container: 'wav', encoding: 'pcm_s16le', sample_rate: 44100 }, language }),
      }));
      if (!response.ok) {
        cancelBody(response.body);
        if (response.status === 429) throw new AgentError(429, 'Speech is busy; try again shortly');
        throw new AgentError(response.status === 503 ? 503 : 502, 'Speech service unavailable');
      }
      const declared = Number(response.headers?.get('content-length'));
      if (Number.isFinite(declared) && declared > MAX_SPEECH_BYTES) {
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
        if (total > MAX_SPEECH_BYTES) throw new AgentError(502, 'Speech response exceeds size limit');
        chunks.push(Buffer.from(value));
      }
      if (controller.signal.aborted) throw new AgentError(timedOut ? 504 : 499, timedOut ? 'Speech synthesis timed out' : 'Speech request canceled');
      const audio = Buffer.concat(chunks, total);
      if (audio.length < 12 || audio.toString('ascii', 0, 4) !== 'RIFF' || audio.toString('ascii', 8, 12) !== 'WAVE') throw new AgentError(502, 'Invalid speech audio');
      return audio;
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
