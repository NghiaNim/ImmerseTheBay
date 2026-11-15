# S25 setup and phone-led practice

User owns a Samsung S25 and requests installation/configuration, with phone rear-camera photography and spoken practice goals as inputs and XREAL glasses as the display. Do not rely on Air 2 Ultra tracking cameras for photos.

Ownership for this task:
- Native lesson integration: `Assets/Scripts/Learning/LearningAgentClient.cs`, contracts and new `Assets/Scripts/Learning/PhonePractice/**` service code. Photo + goal calls the existing portal practice generation, adopts its returned lesson ID/image, and uses existing native lesson/voice runtime. No scene/settings/package edits.
- Phone UI: new `Assets/Scripts/Learning/PhonePractice/**` panel/bootstrap code (coordinate filenames with integration owner). Attach to the existing XREAL phone-controller canvas at runtime; phone capture/typed or spoken goal/start/status, with glasses rendering unchanged. Use existing camera/speech primitives and registered module status. Do not mirror phone UI into the glasses.
- Root: ignored device config, `Assets/Scripts/Visko/ViskoSession.cs` runtime URL override if needed, hardware setup/install, build/verification, docs and integration.

User follow-up ownership (root): fix Mac web clerk speech and pin the original
clerk's appearance in `portal/public/{learn.js,lesson-speech.js}`, relevant
`portal/agent/` continuity, preset, prompt and regression-test files, and
`Assets/StreamingAssets/lessons/ja-konbini-001.json`. Preserve custom-photo
identity and empty shelf scenes; do not impose the demo cast on real-world photos.

Preserve existing user/editor changes. Never edit protected Scenes/XR/ProjectSettings/manifest. No new dependencies. Include generated .meta files. Read existing learning, photo and virtual-controller code before implementation. API keys remain on the portal. Cancel camera/mic/HTTP on disable/leave; never persist a photo unless requested.

Physical device currently reports SM-S931U1, Android16/API36, arm64,4KiB pages. XREAL official documentation warns Android16 compatibility is under investigation; installation success must not be reported as successful AR hardware operation. No OS downgrade or firmware mutation without explicit user instruction.

## App follow-up delivered (2026-11-14)

- Pin the original checkout clerk as the same young adult woman, with an oval
  face, dark brown eyes, dark brown low ponytail and wispy bangs, white collared
  shirt and beige apron. The base lesson and preset use the same descriptor;
  every checkout action and rejoin carries it through the model context and
  final video prompt. Reject action deltas that redefine that appearance.
- Preserve generic photo identity for user uploads. Empty shelf scenes stay
  empty; returning to checkout restores its approved start image and cast.
- Start independent Cartesia clerk speech as soon as the agent decision arrives,
  before waiting for video reset/reseed/frame readiness. Allow the closing reply
  to finish before opening the practice review.
- Verification: portal tests 180/180 and syntax checks passed. Real Cartesia
  returned non-silent audio (2.32 seconds, peak 0.852). Chrome E2E with default
  autoplay restrictions, real agent/TTS API, simulated recognition, and a held
  scene-image response passed opening → detour → rejoin in 23 seconds; four
  speech HTTP responses were 200 and the clerk spoke while video setup waited.
- Live Dynamic test produced a 640x368 opening video, then failed the sandwich
  transition readiness gate. The provider broadcast generation_started and
  chunks 0/1 with zero frames, followed by chunk 2 with 33 frames; the gate
  timed out. Do not claim the complete real video branch/rejoin flow passes.
  Local diagnostics: /tmp/il-identity-live-e2e and /tmp/il-identity-speech-e2e.

## Device checkpoint

- S25 reaches the Mac portal directly over Wi-Fi at port 8793 (HTTP 200).
- Official ControlGlasses 3.1 installed after the user unlocked the phone and
  completed the Play Protect flow. No verification protection was disabled.
- Unity GUI build completed with the updated local portal URL and clerk prompt.
  APK/config are ignored; updated APK installation and glasses validation remain
  with the device sub-agent. Android 16 AR compatibility is still unverified.
