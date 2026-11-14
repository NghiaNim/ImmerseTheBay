import { randomUUID } from 'node:crypto';
import { WebSocket, WebSocketServer } from 'ws';

export const MAX_MESSAGE_BYTES = 1024 * 1024;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const clientTypes = new Set(['hello', 'join', 'leave', 'state', 'event', 'ping']);
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const name = value => typeof value === 'string' && value.trim().length > 0 && value.length <= 256;
function withinDepthLimit(value) {
  const stack = [[value, 0]];
  while (stack.length) {
    const [item, depth] = stack.pop();
    if (item === null || typeof item !== 'object') continue;
    if (depth > 64) return false;
    for (const child of Object.values(item)) stack.push([child, depth + 1]);
  }
  return true;
}

export function createRealtimeServer({ port = 8787, host = '0.0.0.0', server, logger = line => console.log(line) } = {}) {
  // An existing HTTP(S) server also permits TLS termination or in-process transport tests.
  const wss = new WebSocketServer({ ...(server ? { server } : { port, host }), maxPayload: MAX_MESSAGE_BYTES, perMessageDeflate: false });
  const rooms = new Map();
  const log = (event, fields = {}) => logger(JSON.stringify({ ts: Date.now(), event, ...fields }));
  const envelope = (type, data, room, from = 'server') => ({
    type, id: randomUUID(), ts: Date.now(), ...(room ? { room } : {}), from, data,
  });
  const send = (ws, message) => {
    if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(message));
  };
  const broadcast = (room, message, except) => {
    for (const member of room.members) if (member !== except) send(member, message);
  };
  const members = room => [...room.members].map(member => member.clientId);
  const leave = ws => {
    if (!ws.room) return;
    const roomName = ws.room;
    const room = rooms.get(roomName);
    ws.room = null;
    room.members.delete(ws);
    const message = envelope('leave', { clientId: ws.clientId, members: members(room) }, roomName);
    send(ws, message);
    broadcast(room, message);
    log('leave', { clientId: ws.clientId, room: roomName, members: room.members.size });
  };

  wss.on('listening', () => log('listening', { address: wss.address() }));
  wss.on('error', error => log('server_error', { message: error.message }));
  wss.on('connection', ws => {
    ws.clientId = randomUUID();
    ws.identified = false;
    ws.room = null;
    log('connected', { clientId: ws.clientId });
    const reject = (code, message, replyTo) => {
      send(ws, envelope('error', { code, message, ...(replyTo ? { replyTo } : {}) }, ws.room));
      log('rejected', { clientId: ws.clientId, code });
    };

    ws.on('message', (bytes, binary) => {
      if (binary) {
        if (!ws.identified || !ws.room) return reject('not_in_room', 'Join a room before sending binary');
        if (bytes.length < 6 || ![1, 2, 3].includes(bytes[0]) ||
            (bytes[0] !== 3 && bytes.readUInt32LE(1) === 0) ||
            (bytes[0] === 1 && (bytes.length - 5) % 2 !== 0)) {
          return reject('invalid_binary', 'Expected kind + uint32 LE + nonempty payload (PCM16 aligned)');
        }
        for (const member of rooms.get(ws.room).members) {
          if (member !== ws && member.readyState === WebSocket.OPEN) member.send(bytes, { binary: true });
        }
        log('binary', { clientId: ws.clientId, room: ws.room, kind: bytes[0], bytes: bytes.length });
        return;
      }

      let message;
      try { message = JSON.parse(bytes.toString()); }
      catch { return reject('invalid_json', 'Expected a JSON object'); }
      if (!object(message) || !withinDepthLimit(message) || !uuid.test(message.id) || !Number.isSafeInteger(message.ts) || message.ts < 0 ||
          !object(message.data) || (message.room !== undefined && !name(message.room)) ||
          (message.from !== undefined && !name(message.from))) {
        return reject('invalid_envelope', 'Expected type, UUID id, epoch ms ts, optional room/from, and object data');
      }
      const { type, id, data } = message;
      if (!clientTypes.has(type)) return reject('unknown_type', 'Unsupported client message type', id);
      if (type === 'hello') {
        if (ws.identified) return reject('already_identified', 'hello is allowed once per connection', id);
        if (!name(data.clientId) || !name(data.deviceModel) || !name(data.appVersion)) {
          return reject('invalid_hello', 'hello needs clientId, deviceModel, appVersion', id);
        }
        ws.identified = true;
        send(ws, envelope('welcome', { serverTime: Date.now(), clientId: ws.clientId, members: [] }));
        log('hello', { clientId: ws.clientId, deviceModel: data.deviceModel, appVersion: data.appVersion });
        return;
      }
      if (!ws.identified) return reject('hello_required', 'Send hello first', id);
      if (type === 'ping') {
        send(ws, envelope('pong', { clientTs: message.ts, replyTo: id }, ws.room));
        return;
      }
      if (type === 'join') {
        if (!name(message.room)) return reject('invalid_room', 'join needs a room', id);
        if (ws.room !== message.room) leave(ws);
        ws.room = message.room;
        if (!rooms.has(ws.room)) rooms.set(ws.room, { members: new Set(), states: new Map() });
        const room = rooms.get(ws.room);
        room.members.add(ws);
        broadcast(room, envelope('join', { clientId: ws.clientId, members: members(room) }, ws.room));
        for (const state of room.states.values()) send(ws, state);
        log('join', { clientId: ws.clientId, room: ws.room, members: room.members.size, replayed: room.states.size });
        return;
      }
      if (!ws.room || message.room !== ws.room) return reject('not_in_room', 'Message room must match joined room', id);
      if (type === 'leave') return leave(ws);
      if (!name(type === 'state' ? data.key : data.name) || !Object.hasOwn(data, 'value')) {
        return reject('invalid_data', `${type} needs ${type === 'state' ? 'key' : 'name'} and a JSON value`, id);
      }
      // Never trust a client-supplied sender identity. Preserve id/ts for replay/deduplication.
      const outgoing = { type, id, ts: message.ts, room: ws.room, from: ws.clientId, data };
      if (Buffer.byteLength(JSON.stringify(outgoing)) > MAX_MESSAGE_BYTES) {
        return reject('message_too_large', 'Relay envelope exceeds 1 MiB after assigning sender', id);
      }
      if (type === 'state') rooms.get(ws.room).states.set(data.key, outgoing);
      broadcast(rooms.get(ws.room), outgoing, ws);
      log(type, { clientId: ws.clientId, room: ws.room, id, ...(type === 'state' ? { key: data.key } : { name: data.name }) });
    });
    ws.on('close', (code) => {
      leave(ws);
      log('disconnected', { clientId: ws.clientId, code });
    });
    ws.on('error', error => log('socket_error', { clientId: ws.clientId, message: error.message }));
  });

  return {
    wss,
    close: () => {
      for (const ws of wss.clients) ws.terminate();
      return new Promise((resolve, reject) => wss.close(error => error ? reject(error) : resolve()));
    },
  };
}
