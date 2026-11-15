# Unity Cartesia conversation transport

`CartesiaConversationClient` is a runtime-added component. `Connect(url, token)` accepts a **fresh, single-use server-created Cartesia session**, not a provider key. The portal integration owns lesson identity, authoritative revision checks, tool deduplication, committed decisions, and fresh session creation. No automatic reconnect occurs.

Public methods run on the Unity main thread:

- `Connect(string url, string token)` / `Disconnect()`.
- `SendToolResult(string callId, string result, bool isError = false)`; compact UTF-8 result under 16 KB.
- `ToolCall` event provides `VoiceToolCall` with `tool_call_id`, `tool_name`, `expects_response`, and typed `parameters.intent` / `parameters.transcript`.
- `Interrupted` marks `audio_output_clear`: clear unissued work, but preserve already-issued lesson commits.
- `Transcript(role, text)` provides completed turns.
- `StateChanged` emits `off`, `connecting`, `listening`, or `speaking`. `Status` provides human-readable detail. Connect first disconnects an existing connection and may emit `off` before `connecting`.

Network events marshal through a bounded `ConcurrentQueue<Action>` drained in `Update`. A serialized sender bounds outgoing messages to 32; an overflow terminates rather than replaying stale microphone audio. Receive messages are bounded to 1 MiB and playback to four seconds. `audio_output_clear` stops the AudioSource, clears the PCM ring, and restarts silence immediately. Already submitted platform audio buffers can still have device-specific latency.

The component creates its own MicrophoneCapture child at 44100 Hz with 40 ms mono PCM16 chunks, and requests Android permission through that existing capture component. It stops currently recording MicrophoneCapture instances and restores them on disconnect. **The integration must stop native/legacy speech recognition before connecting.** It must also prevent other microphone consumers from starting during conversation. A device that cannot actually capture 44100 Hz fails explicitly rather than sending incorrectly labelled audio; no untested sample-rate converter is hidden in the transport.

Microphone PCM is sent continuously, including silence. Speaker output uses a streaming mono AudioClip at the same rate. **Unity Microphone + AudioSource does not provide acoustic echo cancellation here.** Glasses/headphone output is recommended; real Beam Pro echo, microphone routing, interruption latency, permission flows, suspension, and IL2CPP WebSocket behavior require hardware validation. App suspension disconnects and requires a new portal session afterward.

Compilation and scene integration are verified by the integration owner. This component does not claim real-device E2E verification or Chinese recognition support. Managed agent Japanese/English language configuration is documented in `portal/voice-worker/README.md`.

Protocol source: https://docs.cartesia.ai/line/integrations/websocket-api.md (API version 2026-08-14).
