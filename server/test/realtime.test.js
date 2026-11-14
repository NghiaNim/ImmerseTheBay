import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { createServer } from 'node:http';
import { Duplex } from 'node:stream';
import test from 'node:test';
import { WebSocket } from 'ws';
import { createRealtimeServer, MAX_MESSAGE_BYTES } from '../src/server.js';

function message(type, data = {}, room) {
  return { type, id: randomUUID(), ts: Date.now(), ...(room ? { room } : {}), data };
}

async function fixture(t) {
  const logs = [];
  const tcp = process.env.REALTIME_TEST_TRANSPORT === 'tcp';
  const http = tcp ? undefined : createServer();
  const server = createRealtimeServer({ port: 0, host: '127.0.0.1', server: http, logger: line => logs.push(JSON.parse(line)) });
  if (tcp) await once(server.wss, 'listening');
  t.after(async () => {
    await server.close();
    http?.close();
  });
  const connect = async () => {
    const ws = tcp ? new WebSocket(`ws://127.0.0.1:${server.wss.address().port}`) :
      new WebSocket('ws://in-process.test', { createConnection: () => {
        const [clientSocket, serverSocket] = socketPair();
        http.emit('connection', serverSocket);
        return clientSocket;
      } });
    const inbox = [];
    const pending = [];
    ws.on('message', (bytes, binary) => {
      const value = binary ? Buffer.from(bytes) : JSON.parse(bytes.toString());
      const index = pending.findIndex(waiter => waiter.predicate(value));
      if (index < 0) inbox.push(value);
      else pending.splice(index, 1)[0].resolve(value);
    });
    const next = (predicate = () => true) => {
      const index = inbox.findIndex(predicate);
      if (index >= 0) return Promise.resolve(inbox.splice(index, 1)[0]);
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          const index = pending.indexOf(waiter);
          if (index >= 0) pending.splice(index, 1);
          reject(new Error('Timed out waiting for message'));
        }, 2500);
        const waiter = { predicate, resolve: value => { clearTimeout(timer); resolve(value); } };
        pending.push(waiter);
      });
    };
    await once(ws, 'open');
    return { ws, inbox, next, send: value => ws.send(JSON.stringify(value)) };
  };
  const client = async room => {
    const peer = await connect();
    peer.send(message('hello', { clientId: randomUUID(), deviceModel: 'integration-test', appVersion: '1' }));
    peer.id = (await peer.next(value => value.type === 'welcome')).data.clientId;
    if (room) {
      peer.send(message('join', {}, room));
      await peer.next(value => value.type === 'join' && value.data.clientId === peer.id);
    }
    return peer;
  };
  return { server, logs, connect, client };
}

// Real HTTP upgrade + masked WebSocket frames, without requiring permission to bind a port.
// Use REALTIME_TEST_TRANSPORT=tcp for the same scenarios over loopback TCP.
function socketPair() {
  function socket() {
    return new Duplex({
      read() {},
      write(chunk, encoding, callback) {
        queueMicrotask(() => {
          if (!this.peer.destroyed) this.peer.push(Buffer.from(chunk));
          callback();
        });
      },
      final(callback) {
        queueMicrotask(() => { if (!this.peer.destroyed) this.peer.push(null); callback(); });
      },
      destroy(error, callback) {
        callback(error);
        queueMicrotask(() => { if (!this.peer.destroyed) this.peer.destroy(); });
      },
    });
  }
  const a = socket();
  const b = socket();
  a.peer = b;
  b.peer = a;
  return [a, b];
}

test('join, relay state/events only to peers in room, replay latest state to late joiner', async t => {
  const { client, logs } = await fixture(t);
  const a = await client('bay');
  const b = await client('bay');
  const other = await client('elsewhere');
  const joined = await a.next(value => value.type === 'join');
  assert.deepEqual(new Set(joined.data.members), new Set([a.id, b.id]));
  const first = message('state', { key: 'chair', value: { contentId: 'chair', position: [1, 2, 3] } }, 'bay');
  first.from = 'spoofed';
  a.send(first);
  assert.deepEqual(await b.next(value => value.type === 'state'), { ...first, from: a.id });
  const latest = message('state', { key: 'chair', value: { nested: { text: 'escaped " } \\ 雪', enabled: true }, list: [null, 4] } }, 'bay');
  a.send(latest);
  await b.next(value => value.type === 'state');
  const event = message('event', { name: 'selected', value: { key: 'chair' } }, 'bay');
  a.send(event);
  assert.equal((await b.next(value => value.type === 'event')).id, event.id);
  const c = await client('bay');
  assert.deepEqual(await c.next(value => value.type === 'state'), { ...latest, from: a.id });
  assert.equal(c.inbox.filter(value => value.type === 'state').length, 0);
  assert.equal(c.inbox.some(value => value.type === 'event'), false);
  assert.equal(a.inbox.some(value => value.type === 'state' || value.type === 'event'), false);
  assert.equal(other.inbox.some(value => value.type === 'state' || value.type === 'event'), false);
  assert(logs.some(value => value.event === 'state' && value.key === 'chair'));
});

test('pong echoes client epoch timestamp and correlates request id', async t => {
  const { client } = await fixture(t);
  const a = await client();
  const ping = message('ping');
  a.send(ping);
  const pong = await a.next(value => value.type === 'pong');
  assert.equal(pong.data.clientTs, ping.ts);
  assert.equal(pong.data.replyTo, ping.id);
  assert(pong.ts >= ping.ts);
});

test('binary relay, leave/disconnect membership, room switching and state retained while empty', async t => {
  const { client } = await fixture(t);
  const a = await client('bay');
  const b = await client('bay');
  const audio = Buffer.alloc(9);
  audio[0] = 1;
  audio.writeUInt32LE(48000, 1);
  audio.writeInt16LE(-32768, 5);
  a.ws.send(audio);
  assert.deepEqual(await b.next(Buffer.isBuffer), audio);
  a.send(message('state', { key: 'persist', value: { visible: true } }, 'bay'));
  await b.next(value => value.type === 'state');
  b.send(message('join', {}, 'new-room'));
  const left = await a.next(value => value.type === 'leave');
  assert.deepEqual(left.data.members, [a.id]);
  await b.next(value => value.type === 'join');
  a.ws.close();
  await once(a.ws, 'close');
  const c = await client('bay');
  assert.equal((await c.next(value => value.type === 'state')).data.key, 'persist');
  c.send(message('leave', {}, 'bay'));
  assert.deepEqual((await c.next(value => value.type === 'leave')).data.members, []);
  c.send(message('event', { name: 'oops', value: {} }, 'bay'));
  assert.equal((await c.next(value => value.type === 'error')).data.code, 'not_in_room');
  const d = await client('new-room');
  b.ws.close();
  assert.deepEqual((await d.next(value => value.type === 'leave')).data.members, [d.id]);
});

test('reject malformed envelopes, unknown types, missing hello, invalid payloads and oversized frames', async t => {
  const { client, connect } = await fixture(t);
  const raw = await connect();
  raw.send(message('join', {}, 'bay'));
  assert.equal((await raw.next()).data.code, 'hello_required');
  const a = await client('bay');
  for (const [value, code] of [
    [{ type: 'ping' }, 'invalid_envelope'],
    [message('made-up'), 'unknown_type'],
    [message('welcome'), 'unknown_type'],
    [message('state', { key: 'x' }, 'bay'), 'invalid_data'],
    [message('state', { key: 'x', value: {} }, 'other'), 'not_in_room'],
  ]) {
    a.send(value);
    assert.equal((await a.next(v => v.type === 'error')).data.code, code);
  }
  a.ws.send('{bad json');
  assert.equal((await a.next(v => v.type === 'error')).data.code, 'invalid_json');
  a.ws.send(Buffer.from([1, 0, 0, 0, 0]));
  assert.equal((await a.next(v => v.type === 'error')).data.code, 'invalid_binary');
  const nearLimit = message('state', { key: 'large', value: { text: '' } }, 'bay');
  nearLimit.data.value.text = 'x'.repeat(MAX_MESSAGE_BYTES - Buffer.byteLength(JSON.stringify(nearLimit)));
  a.send(nearLimit);
  assert.equal((await a.next(v => v.type === 'error')).data.code, 'message_too_large');
  const nested = {};
  let cursor = nested;
  for (let i = 0; i < 70; i++) { cursor.next = {}; cursor = cursor.next; }
  a.send(message('state', { key: 'nested', value: nested }, 'bay'));
  assert.equal((await a.next(v => v.type === 'error')).data.code, 'invalid_envelope');
  const closed = once(a.ws, 'close');
  a.ws.send(Buffer.alloc(MAX_MESSAGE_BYTES + 1));
  assert.equal((await closed)[0], 1009);
});

test('state and event values preserve arbitrary JSON, including scalars, arrays and null', async t => {
  const { client } = await fixture(t);
  const a = await client('bay');
  const b = await client('bay');
  const values = [null, [1, { text: '雪' }], 'text', 17, true];
  for (const [index, value] of values.entries()) {
    a.send(message('state', { key: `value-${index}`, value }, 'bay'));
    assert.deepEqual((await b.next(v => v.type === 'state')).data.value, value);
    a.send(message('event', { name: 'value', value }, 'bay'));
    assert.deepEqual((await b.next(v => v.type === 'event')).data.value, value);
  }
  const c = await client('bay');
  for (const value of values) assert.deepEqual((await c.next(v => v.type === 'state')).data.value, value);
});
