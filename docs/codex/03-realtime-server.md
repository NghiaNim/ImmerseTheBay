# Codex task 3 — Real-time message protocol + reference WebSocket server

Branch: `feat/realtime-protocol`

## Context
`Assets/Scripts/Net/NetClient.cs` is a WebSocket client (System.Net.WebSockets, IL2CPP-safe) with auto-reconnect, heartbeat (`{"type":"ping"}`), serialized sends, text/binary, main-thread `OnMessage/OnBinary/OnConnected/OnDisconnected` events, and an echo-latency probe (`{"type":"probe","t":<ms>}`). It currently points at the public echo server `wss://echo.websocket.org`. There is no protocol and no team server yet.

## You own
- `Assets/Scripts/Net/**` (add files freely)
- new top-level folder `server/` (reference server, tests, README)

## Do
1. **Protocol** (`docs` section in `server/README.md` + C# types in `Assets/Scripts/Net/Protocol.cs`): JSON envelope `{ "type": string, "id": string (uuid), "ts": number (ms since epoch), "room": string?, "from": string?, "data": object }`. Message types: `hello` (client id, device model, app version), `welcome` (server time, assigned client id, room members), `join`/`leave` (room), `state` (arbitrary JSON blob broadcast to the room, e.g. a spawned content id + pose), `event` (one-shot), `ping`/`pong` (with client ts echoed for RTT), `error`. Binary frames: 1-byte kind (1 = PCM16 mono audio, 2 = JPEG frame, 3 = reserved) + 4-byte little-endian sample-rate/width + payload.
2. **C# side**: `RealtimeSession` MonoBehaviour that wraps `NetClient`: sends `hello` on connect, handles `welcome`, join room (default from inspector), `SendState(string key, object data)`, `SendEvent(string name, object data)`, typed C# events for incoming messages, RTT from ping/pong (replace the echo probe when connected to the real server, keep NetClient's probe for echo servers), status on HUD via `DebugRegistry`. Use `JsonUtility`-compatible serializable classes or a tiny hand-written JSON writer; do not add packages. Keep `NetClient` public API backward compatible.
3. **Server** (`server/`): Node.js 20+ with the `ws` package (package.json + lockfile), rooms, broadcast of `state`/`event` to other members, last-known `state` per key replayed to new joiners, ping/pong, binary relay to room, basic validation (reject unknown types, max message 1 MB), structured logs. `npm start` listens on `PORT` (default 8787). Include `npm test` with a few integration tests (two clients join a room, state is relayed and replayed to a late joiner, RTT pong).
4. **Config**: server URL is read from `StreamingAssets/net.json` (`{ "url": "ws://<mac-lan-ip>:8787" }`) with fallback to the inspector field; document how to run the server on the dev Mac and connect the Beam Pro over the same Wi-Fi (Android cleartext `ws://` requires `usesCleartextTraffic` — note it as a request for the integration owner rather than editing ProjectSettings/manifest yourself).

## Done when
- `cd server && npm ci && npm test` passes.
- Compile check in `AGENTS.md` passes with no `error CS`.
- In Play mode in the editor with the server running locally, the HUD shows connected, room members, and RTT; `SendState` from one editor instance reaches the server log.
- No secrets in the repo.
