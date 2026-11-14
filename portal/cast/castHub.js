import { WebSocket, WebSocketServer } from 'ws';

const MAX_FRAME_BYTES = 2 * 1024 * 1024;

/**
 * Attach the /cast JPEG relay to an HTTP server. Other upgrade paths are untouched.
 * All other WebSocket endpoints must also use noServer + explicit path routing;
 * ws's { server, path } listener rejects upgrades intended for a different endpoint.
 * Returns a disposer. No new dependencies beyond the portal's existing ws package.
 */
export function attachCast(server, { portalToken = process.env.PORTAL_TOKEN || '' } = {}) {
  const wss = new WebSocketServer({ noServer: true, maxPayload: MAX_FRAME_BYTES, perMessageDeflate: false });
  let publisher = null;
  let disposed = false;

  function sendStatus(ws) {
    if (ws.readyState === WebSocket.OPEN && ws.bufferedAmount === 0) {
      ws.send(JSON.stringify({ type: 'cast_status', casting: publisher?.readyState === WebSocket.OPEN }));
    }
  }

  function broadcastStatus() {
    for (const ws of wss.clients) if (ws.role === 'viewer') sendStatus(ws);
  }

  function reject(socket, code, reason) {
    socket.end(`HTTP/1.1 ${code} ${reason}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n`);
  }

  function upgrade(req, socket, head) {
    let url;
    try { url = new URL(req.url, 'http://localhost'); } catch { return; }
    if (url.pathname !== '/cast') return;
    if (portalToken && url.searchParams.get('portal_token') !== portalToken) {
      reject(socket, 401, 'Unauthorized');
      return;
    }
    const role = url.searchParams.get('role') || 'viewer';
    if (role !== 'viewer' && role !== 'publisher') {
      reject(socket, 400, 'Bad Request');
      return;
    }
    wss.handleUpgrade(req, socket, head, (ws) => wss.emit('connection', ws, role));
  }

  wss.on('connection', (ws, role) => {
    ws.role = role;
    ws.alive = true;
    ws.sending = false;
    ws.on('error', () => {}); // A failed peer must not crash the portal.
    ws.on('pong', () => { ws.alive = true; });
    if (role === 'publisher') {
      if (publisher) {
        ws.close(4409, 'A caster is already connected');
        return;
      }
      publisher = ws;
      broadcastStatus();
    } else sendStatus(ws);

    ws.on('message', (frame, isBinary) => {
      if (ws !== publisher) {
        ws.close(4403, 'Viewers cannot publish');
        return;
      }
      // This endpoint accepts one complete JPEG per binary message, no JSON/base64 envelope.
      if (!isBinary || frame.length < 4 || frame[0] !== 0xff || frame[1] !== 0xd8 ||
          frame[frame.length - 2] !== 0xff || frame[frame.length - 1] !== 0xd9) {
        ws.close(1003, 'Expected a binary JPEG');
        return;
      }
      for (const viewer of wss.clients) {
        if (viewer.role !== 'viewer' || viewer.readyState !== WebSocket.OPEN ||
            viewer.sending || viewer.bufferedAmount > 0) continue;
        // At most one outstanding send per viewer: drop frames instead of accumulating delay.
        viewer.sending = true;
        viewer.send(frame, { binary: true }, (error) => {
          viewer.sending = false;
          if (error) viewer.terminate();
        });
      }
    });

    ws.on('close', () => {
      if (publisher !== ws) return;
      publisher = null;
      broadcastStatus();
    });
  });

  const heartbeat = setInterval(() => {
    for (const ws of wss.clients) {
      if (!ws.alive) { ws.terminate(); continue; }
      ws.alive = false;
      if (ws.readyState === WebSocket.OPEN) ws.ping();
    }
  }, 15_000);
  heartbeat.unref();

  function dispose() {
    if (disposed) return;
    disposed = true;
    clearInterval(heartbeat);
    server.off('upgrade', upgrade);
    server.off('close', dispose);
    for (const ws of wss.clients) ws.terminate();
    wss.close();
  }
  server.on('upgrade', upgrade);
  server.once('close', dispose);
  return dispose;
}
