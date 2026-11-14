# ImmerseTheBay real-time reference server

Node.js 20+ and one runtime dependency, `ws`. This is a trusted-LAN development
server: no authentication, authorization, durable storage, or media decoding.
Room state stays in memory (including after the last member leaves) until the
process restarts. Memory grows with the number of room/state keys; restart to
clear it. Do not expose it to the public Internet as a production service.

## Run on the dev Mac

```sh
cd server
npm ci
npm test
npm start
# Optional port override:
PORT=9000 npm start
```

`npm start` binds `0.0.0.0:8787` by default. Allow Node through the Mac firewall
for the local network. Find the Mac's Wi-Fi IPv4 address in System Settings >
Wi-Fi > Details > TCP/IP. Connect Beam Pro to the same Wi-Fi; disable access
point/client isolation if devices cannot reach each other. Use the Mac's LAN
address on the Beam Pro; `localhost` on the Beam Pro refers to the Beam Pro.
For a remote server use TLS (`wss://`) through an HTTPS reverse proxy or supply
an HTTPS server to `createRealtimeServer({ server })`.

## Unity setup

1. Add `RealtimeSession` next to the scene's existing `NetClient` (the component
   requires it). Set `defaultRoom`, initially `bay`, to the same value on all
   clients. Keep both components enabled.
2. Copy `server/net.example.json` to `Assets/StreamingAssets/net.json`, replacing
   the example address with the Mac's Wi-Fi address. This asset is deliberately
   left for the integration owner because StreamingAssets is outside this task's
   ownership. For editor-only use, set `NetClient.webSocketUrl` to
   `ws://127.0.0.1:8787` instead.
3. `NetClient` loads the optional config with UnityWebRequest before its initial
   connection (including Android's `jar:file://` StreamingAssets path). Missing,
   malformed, empty, or non-WebSocket URLs fall back to the inspector field.
   Disable `loadStreamingAssetsConfig` to use only the inspector. An empty
   fallback URL skips connecting. Set `testUrl` empty to skip the unrelated HTTP
   connectivity demo.
4. The session sends hello, waits for welcome, joins the default room, and sends
   periodic pings. The HUD shows session status, assigned client id, current room,
   member ids/count, and RTT. It joins the default room again on reconnect.
   Explicit room changes last until the next reconnect. Set `defaultRoom` empty
   for manual `JoinRoom` calls. `LeaveRoom` leaves without disconnecting.
5. `RealtimeSession` disables NetClient's legacy heartbeat and echo probes.
   Standalone NetClient retains those settings and the old public API for echo
   servers; do not attach a session when testing a public echo endpoint. The
   session's response timeout reconnects stalled handshakes/joins/pongs when
   `NetClient.autoReconnect` is enabled. Disabling a session disconnects it;
   re-enabling it starts a fresh handshake.

All session APIs must be called from Unity's main thread. NetClient queues
socket callbacks via `ConcurrentQueue<Action>` and drains them in `Update`;
session events therefore also run on the main thread. Marshal microphone/native
background callbacks before calling `SendBinary`. State/event sends require a
welcome and acknowledged room membership; they throw if used too early and are
not buffered across disconnects.

```csharp
// Attach to a component which has a reference to the RealtimeSession.
[System.Serializable]
public class SpawnState
{
    public string contentId;
    public UnityEngine.Vector3 position;
    public UnityEngine.Quaternion rotation;
}

// After OnJoined (where session.Room is nonempty):
await session.SendState("chair-1", new SpawnState {
    contentId = "chair", position = transform.position, rotation = transform.rotation
});
await session.SendEvent("selected", new SpawnState { contentId = "chair" });

// Subscribe in OnEnable; unsubscribe in OnDisable:
session.OnState += message => {
    SpawnState value = message.Read<SpawnState>();
    UnityEngine.Debug.Log(message.Key + ": " + value.contentId);
};
```

`SendState`/`SendEvent` use JsonUtility: pass serializable classes/structs with
public fields. For dictionaries, arrays, scalars, null, or arbitrary nested JSON use
`SendStateJson(key, valueJson)` / `SendEventJson(name, valueJson)`. Incoming
`StateMessage`/`EventMessage` expose the raw `Json`, typed `Read<T>()`, and the
original `Envelope` (id, ts, room, authoritative from). Available typed events:
`OnWelcome`, `OnJoined`, `OnLeft`, `OnState`, `OnEvent`, `OnPong`, `OnError`,
`OnBinary`, and `OnDisconnected`. Membership events include the complete member
list and identify the affected client; they fire for self and peers. Consumers
should inspect `session.Room`/`ClientId` to identify their own acknowledgement.

## Wire protocol (v1)

Text frames are UTF-8 JSON objects, at most **1 MiB (1,048,576 bytes)** including
the envelope. All messages use:

```json
{
  "type": "state",
  "id": "bf14952e-5274-47a4-ab6a-67b115c82668",
  "ts": 1794690000000,
  "room": "bay",
  "from": "server-assigned-client-id",
  "data": { "key": "chair-1", "value": { "contentId": "chair", "position": [1, 0, 2] } }
}
```

`id` is a UUID string; `ts` is nonnegative integer Unix epoch milliseconds
(JavaScript safe integer). `room` and `from` are optional strings. `data` is
always an object. Names/room ids/client metadata are nonblank strings up to 256
UTF-16 code units. JSON nesting is limited to 64 levels. Room and state keys are case-sensitive. The server assigns a
fresh UUID client id per connection, uses `from: "server"` for control messages,
and overwrites client `from` on relays. The hello's client id is a client-local
identifier, not an authenticated identity.

| Type | Direction | `data` / behavior |
| --- | --- | --- |
| `hello` | client → server | `{clientId, deviceModel, appVersion}` strings; first message, once per connection |
| `welcome` | server → client | `{serverTime, clientId, members: []}`; assigned id, epoch ms; no room until join |
| `join` | client → server | `{}`, envelope `room` required; switches from the old room if any |
| `join` | server → room including joiner | `{clientId, members: [id, ...]}`; full membership including self; sent before state replay |
| `leave` | client → server | `{}`, envelope `room` must match current membership |
| `leave` | server → leaver and remaining members | `{clientId, members: [id, ...]}`; sent on explicit leave, switch, or socket close |
| `state` | client → other room members | `{key, value}`; arbitrary JSON value; latest per room/key replaces older value, replayed to new/rejoining members |
| `event` | client → other room members | `{name, value}`; arbitrary JSON value; one-shot, never stored/replayed |
| `ping` | client → server | `{}`; original envelope `ts` is the client timestamp |
| `pong` | server → client | `{clientTs, replyTo}`; exact original ping ts/id; envelope ts is server time |
| `error` | server → client | `{code, message, replyTo?}`; rejected message id when available |

`state`/`event`/`leave` require the envelope room to equal the sender's joined
room. Each connection joins at most one room. State/event relays preserve the
original message `id`, `ts`, `data`, and authoritative `from`; replay sends that
same stored envelope, with no new timestamp. The sender receives no echo.
Different keys replay in insertion order; there is no durable delivery,
acknowledgement, conflict resolution, or deletion operation. Last received state
wins. Applications may encode a tombstone in `value` if needed. Joining the same
room again refreshes membership and replays its current state. Ping is allowed
after hello, even outside a room. It is an application JSON message, separate
from WebSocket control ping/pong. Unity computes RTT with a monotonic clock
after verifying both `replyTo` and `clientTs`, so wall-clock changes do not alter
the measured duration.

Unknown types, server-only types from clients, malformed envelopes, invalid
payloads, and room violations receive an error without being broadcast. Frames
over the limit close the WebSocket with code 1009. Structured JSON log lines
include `ts`, `event`, client/room/key/name metadata as applicable, and no state
or media payload. `state` log entries confirm that Unity sends reached the server.

### Binary frames

Each WebSocket binary message is exactly:

| Offset | Bytes | Meaning |
| --- | --- | --- |
| 0 | 1 | Kind: `1` PCM16 mono, `2` JPEG, `3` reserved |
| 1 | 4 | Unsigned little-endian sample rate in Hz for audio / width in pixels for JPEG |
| 5 | remaining | Nonempty payload; PCM signed 16-bit little-endian, mono, whole samples; JPEG encoded bytes |

The limit includes the 5-byte header. Audio sample rate and JPEG width must be
positive. Kind 3's parameter is uninterpreted; its payload is relayed unchanged.
Binary frames require hello and a joined room; they relay unchanged only to
other room members and are never stored. There is no sender id/timestamp inside
this minimal binary format. The server validates framing/alignment, not media
contents. Use `session.SendBinary(BinaryKind.Pcm16Mono, 48000, pcmBytes)` or
`Protocol.EncodeBinary` / `DecodeBinary`. `OnBinary` exposes decoded kind,
sample-rate/width, and payload.

## Verification and requests for owners

`npm test` runs actual `ws` clients, HTTP upgrades, masked WebSocket frames, and
the production server handlers over paired in-process Duplex streams. It needs
no listening-port permission. It covers room joins, isolation, state replacement
and late replay, event delivery without replay, sender identity, ping/pong,
binary relay, room switch/leave/disconnect, empty-room state retention,
validation, and oversized frame close code 1009. To run the same tests through
real loopback TCP on an unrestricted machine:

```sh
REALTIME_TEST_TRANSPORT=tcp npm test
```

Development sandbox verification: `npm ci && npm test` passes in-process. A TCP
attempt was blocked by the sandbox (`listen EPERM`), so the listener/LAN path
still needs coordinator verification. The networking C# files compile against
the installed Unity 6000.0.85f2 managed assemblies using the .NET compiler; this
is an API/syntax check, not the Unity Android/IL2CPP build. Unity was deliberately
not launched. Play-mode, device HUD, and Beam Pro round-trip checks remain for
the coordinator.

Requests for integration owner:

- Add `RealtimeSession` alongside `NetClient` in the scene generator; set the
  fallback URL/default room, and regenerate the scene.
- Add the configured `StreamingAssets/net.json` asset and Unity-generated meta.
- Generate and commit `.meta` files for the new `Protocol.cs` and
  `RealtimeSession.cs` scripts on import (Unity was not launched for this task).
- Enable Android cleartext traffic (`android:usesCleartextTraffic="true"` in
  the application manifest / appropriate network security config) for LAN
  `ws://` tests; Unity's insecure HTTP setting may also need review for HTTP
  endpoints. Prefer `wss://` for deployed endpoints. No ProjectSettings,
  packages, or Android manifest were changed here.
- Run the Unity compile check from AGENTS.md, then Play mode with two clients in
  the same room: verify HUD connected/member count/RTT, send `SendState` and
  confirm a structured server `state` log plus the peer's `OnState`, then join a
  third client to verify replay. Repeat on Beam Pro over Wi-Fi and test reconnect.

No secrets or Unity package dependencies were added. The server-only `ws`
dependency and lockfile live entirely under this folder.
