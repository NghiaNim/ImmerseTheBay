import { AgentError } from './lessons.js';

export const MAX_AUDIO_BYTES = 10 * 1024 * 1024;
export const DEFAULT_TRANSCRIBE_MODEL = 'gpt-transcribe';
const extensions = new Map([
  ['audio/webm', 'webm'], ['audio/mp4', 'mp4'], ['audio/ogg', 'ogg'],
  ['audio/wav', 'wav'], ['audio/x-wav', 'wav'], ['audio/mpeg', 'mp3'],
]);
export function audioType(value = '') {
  const type = value.split(';', 1)[0].trim().toLowerCase();
  if (!extensions.has(type)) throw new AgentError(415, 'Unsupported audio format');
  return type;
}
function audioLanguage(value) {
  if (value === undefined || value === '') return undefined;
  if (typeof value !== 'string' || !/^(ja(?:-JP)?|zh(?:-CN|-TW|-HK)?|en(?:-US|-GB)?)$/i.test(value)) {
    throw new AgentError(400, 'Unsupported audio language');
  }
  return value.slice(0, 2).toLowerCase();
}

// Audio is never persisted or logged. The provider receives one bounded recording.
export function createTranscriber({ apiKey = process.env.OPENAI_API_KEY,
  model = process.env.OPENAI_TRANSCRIBE_MODEL || DEFAULT_TRANSCRIBE_MODEL,
  fetchImpl = fetch, timeoutMs = 20000 } = {}) {
  return async ({ audio, mimeType, language }) => {
    const type = audioType(mimeType);
    const hint = audioLanguage(language);
    if (!Buffer.isBuffer(audio) || !audio.length) throw new AgentError(400, 'Audio recording is empty');
    if (audio.length > MAX_AUDIO_BYTES) throw new AgentError(413, 'Audio recording exceeds 10 MiB');
    if (!apiKey) throw new AgentError(503, 'Server transcription requires OPENAI_API_KEY');
    const body = new FormData();
    body.append('model', model);
    body.append('file', new Blob([audio], { type }), `speech.${extensions.get(type)}`);
    if (hint) body.append('languages[]', hint);
    let response;
    try {
      response = await fetchImpl('https://api.openai.com/v1/audio/transcriptions', {
        method: 'POST', headers: { Authorization: `Bearer ${apiKey}` }, body,
        signal: AbortSignal.timeout(timeoutMs),
      });
    } catch (error) {
      if (error.name === 'TimeoutError' || error.name === 'AbortError') throw new AgentError(504, 'Transcription timed out; try a shorter recording');
      throw new AgentError(502, 'Transcription service unavailable');
    }
    if (!response.ok) throw new AgentError(response.status === 429 ? 429 : 502,
      response.status === 429 ? 'Transcription is busy; try again shortly' : 'Transcription service unavailable');
    let result;
    try { result = await response.json(); } catch { throw new AgentError(502, 'Invalid transcription response'); }
    if (typeof result?.text !== 'string' || result.text.length > 10000) throw new AgentError(502, 'Invalid transcription response');
    return { text: result.text.trim() };
  };
}
