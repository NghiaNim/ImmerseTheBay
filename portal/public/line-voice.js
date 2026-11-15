export function assertVoiceRevision(authoritative, local) {
  if (!authoritative || !local || typeof local.runId !== 'string' || !local.runId
    || !Number.isSafeInteger(local.revision) || authoritative.runId !== local.runId || authoritative.revision !== local.revision) {
    throw new Error('Lesson state changed or a scene update is uncertain. Leave and restart safely; no commands were replayed.');
  }
}
// Cartesia Managed Agents browser protocol: JSON frames carrying mono PCM16.
export function encodePcm(samples) {
  const bytes = new Uint8Array(samples.length * 2), view = new DataView(bytes.buffer);
  for (let i = 0; i < samples.length; i++) view.setInt16(i * 2, Math.round(Math.max(-1, Math.min(1, samples[i])) * (samples[i] < 0 ? 32768 : 32767)), true);
  let binary = ''; for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary);
}
export function decodePcm(base64) {
  const binary = atob(base64);
  if (binary.length % 2) throw new Error('Invalid voice audio');
  const bytes = Uint8Array.from(binary, c => c.charCodeAt(0)), view = new DataView(bytes.buffer), samples = new Float32Array(bytes.length / 2);
  for (let i = 0; i < samples.length; i++) samples[i] = view.getInt16(i * 2, true) / 32768;
  return samples;
}
export function createLineVoice({ getSession, onTool, onState = () => {}, onText = () => {}, WebSocketImpl = globalThis.WebSocket, AudioContextImpl = globalThis.AudioContext, getUserMedia = constraints => navigator.mediaDevices.getUserMedia(constraints) }) {
  let generation = 0, audioTurn = 0, socket, context, stream, source, processor, silent, ready = false, nextAudio = 0, state = 'off', deadline;
  let chain = Promise.resolve(); const playing = new Set(), tools = new Map();
  const status = (value, message = '') => { state = value; onState({ state, message }); };
  function clearAudio() { for (const node of playing) { node.onended = null; try { node.stop(); } catch {} node.disconnect(); } playing.clear(); nextAudio = 0; }
  function disconnect(message = '') {
    generation++; ready = false; clearTimeout(deadline); clearAudio();
    if (processor) { processor.onaudioprocess = null; processor.disconnect(); } source?.disconnect(); silent?.disconnect();
    processor = source = silent = null;
    for (const track of stream?.getTracks() || []) track.stop(); stream = null;
    if (socket) { socket.onclose = socket.onerror = socket.onmessage = socket.onopen = null; socket.close(1000, 'session completed'); socket = null; }
    void context?.close(); context = null; tools.clear(); chain = Promise.resolve(); status('off', message);
  }
  async function connect() {
    if (state !== 'off') return;
    const own = ++generation; status('connecting');
    const current = () => own === generation;
    try {
      context = new AudioContextImpl({ sampleRate: 44100 }); await context.resume();
      const captured = await getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true }, video: false });
      if (!current()) { for (const track of captured.getTracks()) track.stop(); return; }
      stream = captured;
      if (context.sampleRate !== 44100) throw new Error('This browser cannot open 44.1 kHz voice audio.');
      const session = await getSession(); if (!current()) return;
      const url = new URL(session.url); url.searchParams.set('cartesia_version', session.version); url.searchParams.set('access_token', session.token);
      socket = new WebSocketImpl(url.toString());
      const send = event => { if (current() && socket?.readyState === 1) socket.send(JSON.stringify(event)); };
      deadline = setTimeout(() => { if (current() && !ready) disconnect('Voice connection timed out. Try again.'); }, 15000);
      socket.onopen = () => send({ type: 'session_create', audio: { input_format: 'pcm_44100', output_delivery: 'speaking_pace', continuous_input: true } });
      socket.onerror = () => { if (current()) disconnect('Voice connection failed. Try again.'); };
      socket.onclose = () => { if (current()) disconnect('Voice disconnected. Manual controls are available.'); };
      socket.onmessage = event => {
        if (!current()) return;
        try {
          const data = JSON.parse(event.data);
          if (data.type === 'session_ready') {
            if (ready) return;
            ready = true; clearTimeout(deadline);
            source = context.createMediaStreamSource(stream);
            processor = context.createScriptProcessor(2048, 1, 1); silent = context.createGain(); silent.gain.value = 0;
            processor.onaudioprocess = e => { if (ready && current()) { if (socket.bufferedAmount > 1024 * 1024) { disconnect('Voice network is too slow. Please reconnect.'); return; } send({ type: 'audio_input', audio: encodePcm(e.inputBuffer.getChannelData(0)) }); } };
            source.connect(processor); processor.connect(silent); silent.connect(context.destination); status('listening');
          } else if (data.type === 'audio_output' && ready) {
            const samples = decodePcm(data.audio), buffer = context.createBuffer(1, samples.length, 44100); buffer.copyToChannel(samples, 0);
            const node = context.createBufferSource(); node.buffer = buffer; node.connect(context.destination); playing.add(node);
            nextAudio = Math.max(context.currentTime, nextAudio); node.start(nextAudio); nextAudio += buffer.duration; status('speaking');
            node.onended = () => { playing.delete(node); node.disconnect(); if (current() && !playing.size) status('listening'); };
          } else if (data.type === 'audio_output_clear') { audioTurn++; clearAudio(); status('listening'); }
          else if (data.type === 'turn_ended' && typeof data.text === 'string') onText(data.text, data.role);
          else if (data.type === 'client_tool_call') {
            if (typeof data.tool_call_id !== 'string' || tools.has(data.tool_call_id)) return;
            tools.set(data.tool_call_id, true);
            // Do not evict IDs within a call: a late duplicate must never advance a lesson.
            if (tools.size > 256) { disconnect('Voice call limit reached. Reconnect to continue.'); return; }
            const issuedInTurn = audioTurn;
            chain = chain.then(async () => {
              if (!current()) return;
              if (issuedInTurn !== audioTurn) {
                if (data.expects_response) send({ type: 'client_tool_result', tool_call_id: data.tool_call_id, result: 'Interrupted before execution; no lesson change was issued.', is_error: true });
                return;
              }
              let result, isError = false;
              try { result = await onTool(data, current); } catch (error) { result = { accepted: false, error: error.message }; isError = true; }
              if (data.expects_response && current()) send({ type: 'client_tool_result', tool_call_id: data.tool_call_id, result: JSON.stringify(result), is_error: isError });
            });
          } else if (data.type === 'error') { if (data.fatal !== false) disconnect('Voice service rejected the session. Check agent configuration.'); else onState({ state, message: 'Voice service could not process that input. Try again.' }); }
        } catch { disconnect('Invalid voice response. Reconnect to continue.'); }
      };
    } catch (error) { if (current()) disconnect(error.name === 'NotAllowedError' ? 'Allow microphone access to start conversation.' : error.message); }
  }
  return { connect, disconnect, get state() { return state; } };
}
