// Cartesia audio is fetched through the portal; provider credentials never reach
// the browser. One player owns speech output and restores scene volume on exit.
export function createLessonSpeech({ video, headers = () => ({}), isMicrophoneActive = () => false, onState = () => {} }) {
  let current = null;
  function dispose(operation) {
    if (operation.audio) {
      operation.audio.onended = null; operation.audio.onerror = null;
      operation.audio.pause(); operation.audio.removeAttribute('src'); operation.audio.load();
    }
    if (operation.url) URL.revokeObjectURL(operation.url);
    if (operation.previousVolume !== undefined) video.volume = operation.previousVolume;
    operation.url = null; operation.audio = null; operation.previousVolume = undefined;
  }
  function cancel() {
    const operation = current;
    if (!operation) return;
    current = null; operation.controller.abort();
    operation.finish?.(false); dispose(operation);
    onState({ state: 'idle', role: operation.role });
  }
  async function play({ text, language = 'ja', role = 'learner' }) {
    cancel();
    if (isMicrophoneActive() || !text?.trim()) return false;
    const operation = { controller: new AbortController(), role };
    current = operation;
    onState({ state: 'loading', role });
    try {
      const response = await fetch('/api/agent/speak', {
        method: 'POST', headers: { ...headers(), 'Content-Type': 'application/json' },
        body: JSON.stringify({ text: text.trim(), language }), signal: operation.controller.signal,
      });
      if (!response.ok) {
        const messages = { 401: 'Portal authorization is required.', 429: 'Speech is busy. Try again shortly.', 503: 'Server speech is unavailable.', 504: 'Speech timed out.' };
        throw new Error(messages[response.status] || 'Speech could not be generated.');
      }
      const blob = await response.blob();
      if (current !== operation || isMicrophoneActive()) return false;
      if (!blob.size || !/^audio\//i.test(blob.type)) throw new Error('Speech returned no playable audio.');
      operation.url = URL.createObjectURL(blob);
      operation.audio = new Audio(operation.url);
      operation.previousVolume = video.volume;
      video.volume = operation.previousVolume * 0.15;
      const played = await new Promise((resolve, reject) => {
        operation.finish = resolve;
        operation.audio.onended = () => resolve(true);
        operation.audio.onerror = () => reject(new Error('Speech audio could not be played.'));
        operation.audio.play().then(() => {
          if (current === operation) onState({ state: 'playing', role });
        }, reject);
      });
      if (current === operation) onState({ state: 'idle', role });
      return played;
    } catch (error) {
      if (current === operation && error.name !== 'AbortError') {
        onState({ state: 'error', role, message: error.name === 'NotAllowedError' ? 'Playback was blocked. Press the replay button.' : error.message || 'Speech playback failed.' });
      }
      return false;
    } finally {
      dispose(operation);
      if (current === operation) current = null;
    }
  }
  return { play, cancel };
}
