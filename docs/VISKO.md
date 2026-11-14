# Visko (Reactor) integration

Visko Orbis models run on Reactor (docs.reactor.inc) and stream real-time generated video over **WebRTC**. Both models are supported:

| Model | Slug | Notes |
|---|---|---|
| Dynamic (default) | `reactor/visko-orbis-dynamic` | `max_chunks` up to 229 (~7 min/run). Published schema lists `current_prompt` / `active_prompt`, but the 2026-11-14 live deployment omitted both; track acceptance separately. No `passthrough` on `set_prompt`. |
| Stable | `reactor/visko-orbis-stable` | up to 2000 chunks (~61 min/run). `set_prompt` accepts `passthrough`. |

Live delivery depends on the model/deployment: Dynamic was verified at native **640×368**, 18 fps with audio, in 33-frame chunks (~1.8 s). Glasses request native before every start. The learning web page also prefers native, but the user approved a **web-only 1080p fallback** when native is unavailable (observed on Stable). Inspect `available_resolutions`; do not hard-code native dimensions. `set_prompt` steers an ongoing run; `set_audio_prompt` describes sound. The web trace distinguishes `acceptedPrompt` from applied-prompt telemetry and records delivered dimensions, frame and audio sample counts. Current Dynamic chunks report 33 frames and 88,000 audio samples; audio was measured non-silent in the browser. Billing runs from ready until Leave, even when idle.

## Where the API key goes
**`portal/.env`** (gitignored; copy from `portal/.env.example`):
```
REACTOR_API_KEY=rk_...
```
The key never ships in the app or the browser. The portal mints short-lived, session-scoped JWTs (`POST https://api.reactor.inc/tokens`) for both the web page and the Unity app.

## Pieces
| Piece | Path | Role |
|---|---|---|
| Portal server | `portal/server.js` | `POST /api/token` (JWT, optional `sessionId` binding), `WS /sync` hub, serves the test page. `npm start`, port 8790. |
| Web quick-test page | `portal/public/` | Official JS SDK (`@reactor-team/js-sdk@3.4.0`): pick model, create/join session, prompt, start/pause/resume/reset, audio prompt, resolution, seed, live video + audio. |
| Unity session | `Assets/Scripts/Visko/ViskoSession.cs` | Gets tokens from the portal, connects through the Android bridge, renders frames on "Visko Screen", mirrors state to the hub, executes relayed commands. Menu buttons "Visko: …". HUD section "VISKO". |
| Android bridge | `Assets/Plugins/Android/ViskoReactorBridge.kt` + `mainTemplate.gradle` (`inc.reactor:reactor-sdk-android:1.1.0`) | Wraps the Kotlin SDK (coroutines/flows) for `AndroidJavaObject`; copies latest BGRA frame; plays `main_audio` with `AudioTrack`. |
| Device config | `Assets/StreamingAssets/visko.json` (gitignored; template `visko.example.json`) | `portalUrl` (your Mac's LAN IP), `portalToken`, `model`, `autoJoin`, `defaultPrompt`. |

## How sync works
Reactor sessions accept several clients: one creates the session, others join with the session id and a token bound to it; each connection receives `main_video` independently, so web and glasses show the **same generated stream**.
1. Whoever creates a session (web "New session" or glasses "Visko: new session") announces `{type:"session", sessionId, model}` on `/sync`.
2. The other side joins: the glasses auto-join web sessions (`autoJoin`); the web shows "Join Unity session".
3. Commands from either side go straight to Reactor. With "Send commands through Unity" ticked, the web relays commands over `/sync` and the glasses execute them on their connection (useful if a joined client is not allowed to steer).
4. The glasses mirror the model `state` to the hub so the web shows it even when not connected.

## Run it
```bash
cd portal && npm install && cp .env.example .env   # then put REACTOR_API_KEY in .env
npm start                                            # prints the LAN URL
```
Open `http://localhost:8790`, put the printed LAN URL into `Assets/StreamingAssets/visko.json`, build the APK, same Wi-Fi for Mac and Beam Pro.

## Limits / open points
- Editor play mode has no Reactor video (native SDK is Android-only); the editor still syncs with the portal and can relay commands.
- Quotas: 5 concurrent sessions and 10 new sessions/min per account. A session cannot outlive its token (portal mints 6 h tokens).
- The docs don't state explicitly whether a joined client may send commands; the relay path covers that case. Verify on first real run.
- Frames cross JNI as a byte[] copy each frame; keep resolution at `native` (832×480) in the glasses for performance.
