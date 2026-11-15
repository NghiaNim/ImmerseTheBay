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
Root also owns `portal/public/scene-transition.js` and its regression tests to
resolve the real Dynamic frame-readiness failure observed during this work.

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
- Verification: portal tests 181/181 and syntax checks passed. Real Cartesia
  returned non-silent audio (2.32 seconds, peak 0.852). Chrome E2E with default
  autoplay restrictions, real agent/TTS API, simulated recognition, and a held
  scene-image response passed opening → detour → rejoin in 23 seconds; four
  speech HTTP responses were 200 and the clerk spoke while video setup waited.
- The first live Dynamic test exposed a transition gate bug: chunks 0/1 had
  zero frames, followed by chunk 2 with 33 frames, so requiring pixels in chunk
  zero timed out. The gate now retains the observed generation boundary across
  contiguous empty warm-up chunks before waiting for a fresh decoded frame.
- After the fix, real Dynamic checkout → sandwiches → drinks → checkout passed
  in 50.295 seconds at 640x368, with no page errors and revisions 1–5. All three
  transitions became ready on chunk 2. Inspected the captured shelf frames and
  opening/rejoin clerk: the expected female appearance and outfit were retained
  in these samples. This is a successful sample, not a guarantee of future
  identity consistency or per-frame prompt attribution (provider telemetry is
  absent). Local evidence: /tmp/il-identity-live-fixed-e2e/live-report.json;
  /tmp/il-identity-speech-e2e/practice-report.json.

## Device checkpoint

- S25 reaches the Mac portal directly over Wi-Fi at port 8793 (HTTP 200).
- Official ControlGlasses 3.1 installed after the user unlocked the phone and
  completed the Play Protect flow. No verification protection was disabled.
- Unity GUI build completed with the updated local portal URL and clerk prompt;
  updated main APK installed successfully. APK/config remain ignored.
- Wireless ADB connected after user pairing; device model and portal HTTP 200
  were verified using the wireless serial with no USB device present. It supports
  further installs while the USB-C port is occupied by the glasses.
- Both APKs pass ZIP 16KB alignment but contain third-party native libraries
  with only 4KB ELF LOAD alignment. ZIP alignment cannot fix their ELF segments.
  The phone currently runs 4KB pages; full Android 16 AR compatibility is still
  unverified. Do not claim these binaries support 16KB pages.
- A direct main-app launch produced NotFindRuntime: runtime service discovery
  returned an empty package, then fallback libnr_api.so was absent. The service
  exists in ControlGlasses; next verify with glasses attached and launching via
  ControlGlasses, including the required overlay permission. Do not copy random
  runtime libraries into the app as a workaround.
