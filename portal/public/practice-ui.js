// Review labels describe transcript matching, never acoustic pronunciation quality.
export function reviewMetrics(summary) {
  if (!summary) return [];
  const percent = value => typeof value === 'number' && Number.isFinite(value) ? `${Math.round(value)}%` : '—';
  return [
    ['Targets practiced', `${summary.targetsAttempted ?? 0} / ${summary.targetsEncountered ?? 0}`],
    ['Targets completed', String(summary.targetsCompleted ?? 0)],
    ['Transcript match', summary.scoredAttempts ? `${percent(summary.averageTranscriptMatch)} · ${summary.scoredAttempts} attempts` : '— · no scored attempts'],
    ['First-attempt success', summary.firstAttempt?.total ? `${percent(summary.firstAttempt.rate)} · ${summary.firstAttempt.successes}/${summary.firstAttempt.total} targets` : '— · no scored targets'],
    ['Hints used', String(summary.hints ?? 0)],
    ['Skipped targets', String(summary.manualSkips ?? 0)],
  ];
}
export function renderPracticeReview(root, summary, { title = 'Practice review', error = '' } = {}) {
  const doc = root.ownerDocument;
  const el = (tag, text, className) => { const node = doc.createElement(tag); node.textContent = text; if (className) node.className = className; return node; };
  root.replaceChildren(el('p', 'YOUR PRACTICE', 'eyebrow'), el('h1', title), el('p', error || 'A few small steps toward your next conversation.', 'muted'));
  if (!summary) { root.append(el('p', 'No practice results are available for this session.')); return; }
  const metrics = el('dl', '', 'review-metrics');
  for (const [label, value] of reviewMetrics(summary)) { const item = el('div', ''); item.append(el('dt', label), el('dd', value)); metrics.append(item); }
  root.append(metrics, el('p', 'Transcript match compares recognized words with the target sentence. It does not measure pronunciation. Intent requests, hints and skipped lines are not scored attempts.', 'muted'));
  root.append(el('h2', 'Words you encountered'));
  const words = el('ul', '', 'review-words');
  for (const word of summary.words || []) { const item = el('li', ''); item.append(el('strong', word.jp || ''), el('span', word.kana || ''), el('span', word.en || '')); words.append(item); }
  root.append(words.childElementCount ? words : el('p', 'No words recorded yet.', 'muted'));
  root.append(el('h2', 'Keep practicing'));
  for (const target of summary.review || []) {
    const item = el('article', '', 'review-target');
    item.append(el('h3', target.jp || ''), el('p', target.romaji || '', 'reading'), el('p', target.en || target.zh || '', 'muted'));
    item.append(el('small', target.attempts ? `${target.attempts} attempts · best transcript match ${typeof target.bestTranscriptMatch === 'number' ? Math.round(target.bestTranscriptMatch) + '%' : '—'}` : 'Try saying this line aloud next time.'));
    root.append(item);
  }
  if (!summary.review?.length) root.append(el('p', summary.scoredAttempts ? 'Try a new situation, or repeat the scene with fewer hints.' : 'Start with one sentence and say it aloud to get feedback.', 'muted'));
}
export function createGoalRecorder({ transcribe, onState, onText, getUserMedia = () => navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true }, video: false }), Recorder = globalThis.MediaRecorder }) {
  let generation = 0, recording, pending = false, abort;
  function cancel() {
    generation++; pending = false; abort?.abort(); abort = null;
    if (recording) { const old = recording; recording = null; clearTimeout(old.timer); old.recorder.onstop = null; if (old.recorder.state !== 'inactive') old.recorder.stop(); for (const track of old.stream.getTracks()) track.stop(); }
    onState('idle');
  }
  async function start() {
    if (pending || recording) return;
    const own = ++generation; let captured; pending = true; onState('permission');
    try {
      const stream = captured = await getUserMedia();
      if (own !== generation) { for (const track of stream.getTracks()) track.stop(); return; }
      const recorder = new Recorder(stream), chunks = [];
      recording = { stream, recorder }; pending = false;
      recorder.ondataavailable = event => { if (event.data.size) chunks.push(event.data); };
      recorder.onerror = () => { if (own === generation) { cancel(); onState('error', 'Could not record. Type your goal instead.'); } };
      recorder.onstop = async () => {
        for (const track of stream.getTracks()) track.stop();
        if (own !== generation) return;
        clearTimeout(recording?.timer); recording = null; pending = true; onState('transcribing'); abort = new AbortController();
        try { const text = await transcribe(new Blob(chunks, { type: recorder.mimeType || 'audio/webm' }), abort.signal); if (own === generation) onText(text); }
        catch (error) { if (own === generation) onState('error', error.message); }
        finally { if (own === generation) { pending = false; abort = null; onState('idle'); } }
      };
      recorder.start(); recording.timer = setTimeout(stop, 15000); onState('recording');
    } catch (error) { for (const track of captured?.getTracks() || []) track.stop(); if (own === generation) { cancel(); onState('error', error.name === 'NotAllowedError' ? 'Allow microphone access, or type your goal.' : 'Microphone unavailable. Type your goal instead.'); } }
  }
  function stop() { if (recording?.recorder.state === 'recording') recording.recorder.stop(); else cancel(); }
  return { start, stop, cancel };
}
