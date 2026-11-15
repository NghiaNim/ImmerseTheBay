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

## Native startup blocker confirmed on S25

The updated 268 MB APK was reinstalled successfully using wireless ADB while
USB was disconnected from the Mac. After attaching Air 2 Ultra, Android reports
an external 1920x1080 display named `Air 2 Ultra`. ControlGlasses has Camera,
Microphone and USB-device access, but its own initialization fails before the
Unity application can start XR.

Read-only inspection of the official ControlGlasses 3.1 APK confirms that the
non-Beam path in `DisplayModel._isHdmi()` checks whether the display name contains
`HDMI`. The actual name does not; device logs show `not HDMI`, `hasHdmi=false`,
then `sdkFailed` after retries. Replugging reproduces it. The Samsung DeX setting
was changed to 0 as a reversible diagnostic, but the system reset it to 1 on
reconnection. Do not report DeX as permanently disabled or AR as working.

This is a vendor display-detection compatibility blocker with this device/OS
combination. The initial diagnosis was obtained without modifying the official
APK. Keep proprietary disassembly and full device logs local; do not commit
vendor binaries or change OS/firmware. The user subsequently authorized the
local compatibility-patch investigation described below.
The official XREAL docs still warn about Android 16 compatibility:
https://docs.xreal.com/Getting%20Started%20with%20XREAL%20SDK

Wireless deployment remains usable with the glasses connected:

```sh
adb connect PHONE_IP:DEBUG_PORT
adb -s PHONE_IP:DEBUG_PORT install -r Builds/ImmerseTheBay.apk
```

The connection port comes from Wireless debugging's main page, not its pairing
popup, and may change after restarting debugging or reconnecting Wi-Fi. Installing
an update stops the running app; launch it again afterwards.

## Authorized local ControlGlasses compatibility investigation

The user explicitly paused the web fallback and asked to tackle a ControlGlasses
fix. Root owns local patch tooling/notes under `tools/controlglasses/` and this
brief. Build/decoded/vendor artifacts stay under ignored `Builds/` or `/tmp`.
Prepare a minimal display-detection compatibility patch and validate package,
signing and service dependencies before any on-device replacement. Retain the
unaltered official APK for rollback. Do not alter signature checks, SDK licensing,
OS/firmware, security protections or unrelated native libraries. A local re-signed
build is experimental and must not be described as an official vendor release.

The prepared patch is documented in `tools/controlglasses/README.md`. It accepts
the exact `Air 2 Ultra` name only on a non-primary display, retaining the original
HDMI/Beam paths and later product/vendor checks. A signed APK was re-decoded and
compared: only the intended method changed; Android Manifest, resources and all
15 native libraries retained their original bytes. Signature metadata changed
as expected. Local APK SHA-256:
`8e1f7e824fee7455ee55ac4eba06d5581cd643e313cee3799e9ffa2091b8331b`.

Wireless replacement installation succeeded. Both preferences files were
restored byte-for-byte to the new app UID before first launch (mode 600), including
`OTA_SKIP=true`, and previously authorized camera/microphone permissions were
restored. ControlGlasses now shows green `SDK ready`; device logs confirm
`hasHdmi=true` and `sdkSuccess`. Unity discovers `com.xreal.glassescontrol` as its
runtime server, loads NRSDK, and starts IMU, camera and perception services.

The main Unity APK embeds a second copy of the same display-detection method.
Its unpatched copy still reports `isHdmi=false` and `NativeDisplay Start 12`.
The same narrow patch was applied there, signed with the original main-app
development key, and passed signature/ZIP alignment and semantic DEX comparison.
Only `classes.dex` changes in the final APK. Local result and reproducible steps
are documented in `tools/controlglasses/README.md`; this patch must be reapplied
after a fresh Unity build until upstream fixes display detection.

Samsung's actual Settings → Samsung DeX → Connected display → **Mirrored**
selection succeeded; the glasses returned to 3840×1080 at 60 Hz and the setting
remained off for DeX. This supersedes the earlier unsuccessful raw-setting
diagnostic. Main-app rendering verification is still in progress; the successful
Control runtime startup alone does not establish complete AR operation.
