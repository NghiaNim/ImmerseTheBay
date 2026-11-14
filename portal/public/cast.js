const canvas = document.querySelector('#view');
const context = canvas.getContext('2d');
const status = document.querySelector('#status');
const stats = document.querySelector('#stats');
const token = document.querySelector('#token');
const connectButton = document.querySelector('#connect');
const disconnectButton = document.querySelector('#disconnect');
const flip = document.querySelector('#flip');
let socket = null;
let wanted = false;
let retryTimer;
let retryDelay = 1000;
let decoding = false;
let pending = null;
let frames = 0;
let bytes = 0;
let lastFrame = 0;
let lastStats = performance.now();
let casting = false;

function clearView() {
  context.clearRect(0, 0, canvas.width, canvas.height);
  frames = bytes = lastFrame = 0;
  stats.textContent = '0.0 fps · 0 KiB/s';
}

function openSocket() {
  clearTimeout(retryTimer);
  if (!wanted) return;
  status.textContent = 'Connecting…';
  const url = new URL('/cast', location.href);
  url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  url.searchParams.set('role', 'viewer');
  if (token.value) url.searchParams.set('portal_token', token.value);
  const ws = new WebSocket(url);
  ws.binaryType = 'blob';
  socket = ws;
  ws.onopen = () => {
    if (socket !== ws) return;
    retryDelay = 1000;
    status.textContent = 'Connected · waiting for caster';
  };
  ws.onmessage = (event) => {
    if (socket !== ws) return;
    if (typeof event.data === 'string') {
      let message;
      try { message = JSON.parse(event.data); } catch { return; }
      if (message.type !== 'cast_status') return;
      casting = message.casting === true;
      if (!casting) {
        pending = null;
        clearView();
        status.textContent = 'Connected · casting is off';
      } else status.textContent = 'Connected · waiting for frame';
      return;
    }
    casting = true;
    pending = { blob: event.data, ws };
    void drawLatest();
  };
  ws.onerror = () => { if (socket === ws) status.textContent = 'Connection failed · check portal token and /cast setup'; };
  ws.onclose = () => {
    if (socket !== ws) return;
    socket = null;
    casting = false;
    pending = null;
    clearView();
    status.textContent = wanted ? 'Disconnected · retrying (check token and /cast setup)' : 'Disconnected';
    if (wanted) {
      retryTimer = setTimeout(openSocket, retryDelay);
      retryDelay = Math.min(retryDelay * 2, 15_000);
    }
  };
}

async function drawLatest() {
  if (decoding) return;
  decoding = true;
  try {
    while (pending) {
      const { blob, ws } = pending;
      pending = null;
      let bitmap;
      try {
        bitmap = await createImageBitmap(blob);
        if (socket !== ws || !casting) continue;
        canvas.width = bitmap.width;
        canvas.height = bitmap.height;
        context.save();
        if (flip.checked) {
          context.translate(0, canvas.height);
          context.scale(1, -1);
        }
        context.drawImage(bitmap, 0, 0);
        context.restore();
        lastFrame = performance.now();
        frames++;
        bytes += blob.size;
        status.textContent = 'Live · virtual left eye';
      } catch {
        if (socket === ws) status.textContent = 'Could not decode JPEG frame';
      } finally { bitmap?.close(); }
    }
  } finally { decoding = false; }
}

document.querySelector('#connection').addEventListener('submit', (event) => {
  event.preventDefault();
  if (wanted) return;
  wanted = true;
  connectButton.disabled = true;
  disconnectButton.disabled = false;
  token.disabled = true;
  openSocket();
});

function disconnect() {
  wanted = false;
  clearTimeout(retryTimer);
  const ws = socket;
  socket = null;
  ws?.close();
  casting = false;
  pending = null;
  clearView();
  status.textContent = 'Disconnected';
  connectButton.disabled = false;
  disconnectButton.disabled = true;
  token.disabled = false;
}
disconnectButton.addEventListener('click', disconnect);
window.addEventListener('pagehide', disconnect);

setInterval(() => {
  const now = performance.now();
  const seconds = (now - lastStats) / 1000;
  stats.textContent = `${(frames / seconds).toFixed(1)} fps · ${(bytes / seconds / 1024).toFixed(1)} KiB/s`;
  frames = bytes = 0;
  lastStats = now;
  if (socket?.readyState === WebSocket.OPEN && casting && lastFrame && now - lastFrame > 3000) {
    status.textContent = 'Stale · no frame for three seconds';
  }
}, 1000);
