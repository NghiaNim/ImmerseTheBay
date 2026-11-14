import assert from 'node:assert/strict';
import http from 'node:http';
import { once } from 'node:events';
import { Duplex } from 'node:stream';
import test from 'node:test';
import { WebSocket, WebSocketServer } from 'ws';
import { attachCast } from './castHub.js';

// Minimal wire fixture: the relay checks JPEG framing, while the browser decodes the image.
const jpeg = Buffer.from([0xff, 0xd8, 0x01, 0x02, 0xff, 0xd9]);

// Exercise real HTTP upgrades and ws framing without requiring a sandbox TCP listener.
function sockets() {
  let a, b;
  function endpoint(peer) {
    const stream = new Duplex({
      read() {},
      write(chunk, _encoding, done) { peer().push(Buffer.from(chunk)); done(); },
      final(done) { peer().push(null); done(); },
      destroy(error, done) {
        if (!peer().destroyed) peer().push(null);
        done(error);
      },
    });
    stream.setTimeout = stream.setNoDelay = stream.setKeepAlive = () => stream;
    return stream;
  }
  a = endpoint(() => b);
  b = endpoint(() => a);
  return [a, b];
}

async function fixture(t, portalToken = '') {
  const server = http.createServer((_req, res) => res.end('ok'));
  const dispose = attachCast(server, { portalToken });
  const peers = [];
  const sync = new WebSocketServer({ noServer: true });
  server.on('upgrade', (req, socket, head) => {
    if (new URL(req.url, 'http://localhost').pathname !== '/sync') return;
    sync.handleUpgrade(req, socket, head, (ws) => ws.send('sync works'));
  });
  t.after(() => {
    for (const ws of peers) ws.terminate();
    for (const ws of sync.clients) ws.terminate();
    dispose();
    dispose(); // Disposing twice is safe.
    sync.close();
    server.close();
  });
  function createClient(path) {
    const [client, incoming] = sockets();
    server.emit('connection', incoming);
    const ws = new WebSocket(`ws://localhost${path}`, { createConnection: () => client });
    peers.push(ws);
    return ws;
  }
  async function connect(path) {
    const ws = createClient(path);
    const messages = [];
    const closed = new Promise((resolve) => ws.once('close', (...args) => resolve(args)));
    ws.on('message', (data, binary) => messages.push({ data, binary }));
    await once(ws, 'open', { signal: AbortSignal.timeout(2000) });
    async function next() {
      if (!messages.length) await once(ws, 'message', { signal: AbortSignal.timeout(2000) });
      return messages.shift();
    }
    return { ws, next, closed };
  }
  return { createClient, connect };
}

test('relays binary JPEGs to every viewer and announces publisher lifecycle', { timeout: 5000 }, async (t) => {
  const { connect } = await fixture(t);
  const a = await connect('/cast');
  const b = await connect('/cast?role=viewer');
  assert.equal(JSON.parse((await a.next()).data).casting, false);
  assert.equal(JSON.parse((await b.next()).data).casting, false);
  const publisher = await connect('/cast?role=publisher');
  assert.equal(JSON.parse((await a.next()).data).casting, true);
  assert.equal(JSON.parse((await b.next()).data).casting, true);
  publisher.ws.send(jpeg);
  for (const viewer of [a, b]) {
    const message = await viewer.next();
    assert.equal(message.binary, true);
    assert.deepEqual(message.data, jpeg);
  }
  publisher.ws.close();
  assert.equal(JSON.parse((await a.next()).data).casting, false);
  assert.equal(JSON.parse((await b.next()).data).casting, false);
  const replacement = await connect('/cast?role=publisher');
  assert.equal(replacement.ws.readyState, WebSocket.OPEN);
  assert.equal(JSON.parse((await a.next()).data).casting, true);
});

test('requires the portal token for both roles and leaves /sync upgrades alone', { timeout: 5000 }, async (t) => {
  const { createClient, connect } = await fixture(t, 'local-test-token');
  for (const role of ['viewer', 'publisher']) {
    const ws = createClient(`/cast?role=${role}`);
    const [error] = await once(ws, 'error', { signal: AbortSignal.timeout(2000) });
    assert.match(error.message, /401/);
  }
  const viewer = await connect('/cast?portal_token=local-test-token');
  assert.equal(JSON.parse((await viewer.next()).data).casting, false);
  const publisher = await connect('/cast?role=publisher&portal_token=local-test-token');
  assert.equal(publisher.ws.readyState, WebSocket.OPEN);
  const sync = await connect('/sync');
  assert.equal((await sync.next()).data.toString(), 'sync works');
});

test('rejects viewer uploads, duplicate publishers, and invalid JPEG messages', { timeout: 5000 }, async (t) => {
  const { connect } = await fixture(t);
  const publisher = await connect('/cast?role=publisher');
  const duplicate = await connect('/cast?role=publisher');
  assert.equal((await duplicate.closed)[0], 4409);
  const viewer = await connect('/cast');
  const viewerClosed = once(viewer.ws, 'close', { signal: AbortSignal.timeout(2000) });
  viewer.ws.send(jpeg);
  assert.equal((await viewerClosed)[0], 4403);
  const publisherClosed = once(publisher.ws, 'close', { signal: AbortSignal.timeout(2000) });
  publisher.ws.send('not JPEG');
  assert.equal((await publisherClosed)[0], 1003);
});

test('limits payload size before relaying', { timeout: 5000 }, async (t) => {
  const { connect } = await fixture(t);
  const publisher = await connect('/cast?role=publisher');
  const closed = once(publisher.ws, 'close', { signal: AbortSignal.timeout(2000) });
  publisher.ws.send(Buffer.alloc(2 * 1024 * 1024 + 1));
  assert.equal((await closed)[0], 1009);
});
