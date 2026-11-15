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
adb -s PHONE_IP:DEBUG_PORT install --no-incremental -r APK_PATH
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

The patched main APK was installed using complete streaming. Its installed hash
matches the prepared artifact and PackageManager reports `Package is fully
loaded`. The preceding incremental installation returned success, then its
DataLoader failed and Android automatically removed the main package, clearing
its local data. The full reinstall has a new UID and no previous preferences;
the portal configuration remains embedded in the APK. ControlGlasses and its
restored preferences are unaffected. Use `--no-incremental` for subsequent
wireless deployments. Final native rendering verification is waiting for the
user to unlock the phone. Screen timeout was temporarily extended from 30
seconds to 10 minutes for this verification; restore it when finished.

## Native rendering and official baseline checkpoint

The SDK's existing application metadata `com.xreal.debug.noMultiResume=true`
allows native rendering initialization with Samsung Mirrored enabled and DeX
disabled. A local APK containing only this additional manifest configuration
(relative to the display-patched APK) passed signature/alignment and content
checks. NativeDisplay and NativeRendering report Start Finish and submit both
eye viewports, but the user confirms black glasses output and a cropped phone
controller. This is not a completed AR fix. The ignored test artifact is
`Builds/controlglasses-patch/ImmerseTheBay-local-no-multiresume.apk`, SHA-256
`ecce956d15fa86b878dbbf028655bcdf3aa5a85b25961e3daf7890ae23666206`.

User requested an unmodified HelloMR baseline before further app changes. The
XREAL developer release at
https://github.com/dengxian-xreal/XREALSDKTemplate/releases/tag/3.0.0
provides HelloMR.apk (SDK 3.0.0), SHA-256
`de97c23a7eb5b9bf64b8ce6185b92a2410ecc11515e84c50f261059ccda44119`.
Installed unchanged as `com.DefaultCompany.XREALSDKTemplate`. Its phone controller
is complete, but glasses capture is black and runtime reports
`NativeRendering+FrameWait` Failure after successful display/rendering startup.
It loads the current ControlGlasses 3.1 runtime, an explicit version difference;
no ABI mismatch has been established. The current task is to run the original
HelloMR successfully, using a matching-version baseline if needed.

## Permanent official MR reference — user requirement

Keep the official MR baseline permanently available as a separate, version-pinned
reference application. Do not change its scene structure, hierarchy, camera rig,
controllers or rendering structure, and do not add product features to it. Record
source commit, SDK/Unity versions, build-only compatibility adjustments, APK hash
and package ID. A build from unchanged official scene/scripts must be described
as a local baseline build, not a byte-identical official distributed APK.

Develop product features in a separate copy through independent modules while
preserving the official MR structure. Retain the official phone controller UI;
add course management, phone photo capture and practice intent, vocabulary and
scene review, then conversation/video integration incrementally. Verify actual
S25 UI, glasses rendering and interaction at each step before advancing. The
user's report of sky in the official 3.1 XRI sample establishes visible content,
not complete HelloMR or interaction acceptance. Complete that baseline first.

## Accepted baseline selection (supersedes HelloMR-first experiment)

The user explicitly selected exactly two installed applications: (1) the working,
unmodified official SDK 3.1 XRI demo as the permanent visible-scene baseline,
and (2) ImmerseTheBay as the development app. ControlGlasses remains required
runtime infrastructure. Remove the extra local HelloMR test application; retain
its artifacts only as historical diagnostic evidence, not as an active baseline.

Baseline package: `com.DefaultCompany.XREALSDKTemplate`, official release
https://github.com/dengxian-xreal/XREALSDKTemplate/releases/tag/3.1.0 .
Reference source: tag 3.1.0, commit `91806e6`; preserve a pristine source snapshot
separately from the Unity-upgraded experimental checkout. The baseline has
visibly rendered stereo sky and interaction panels; the user confirmed sky.
It was restored after the local HelloMR test showed black output. Do not replace
it with future experimental builds or change its scene/rig/controller structure.
All product functionality belongs in `com.immersethebay.app`; compare each
increment against the preserved baseline on the actual phone and glasses.

## Dev scene and phone controller checkpoint

Built the XRI Starter Assets DemoScene from the isolated SDK 3.1 template
checkout under Builds/helloMR-baseline. Its scene bytes matched the read-only
reference. The development build uses Unity 6000.0.85f2, the template's built-in
render pipeline, and package com.immersethebay.app. It has no course UI yet.
The working official APK remains installed separately.

Build entry point DevReferenceBuild.Build, log /tmp/il-xri-dev-build.log.
Unity reported Succeeded, ERRORS=0. The ignored APK is
Builds/official-hellomr/ImmerseTheBay-XRI-reference-dev.apk, SHA-256
495e7ab9dc1ac733f617ebe8faca601370c13ebf55cfc4f0f3f09edc044f3f62.
Its signer matches the previous dev APK. Streamed replacement via wireless ADB
succeeded. Launched NRXRActivity while ControlGlasses reported SDK ready.
NativeDisplay and NativeRendering both reported Start Finish.

The 3840×1080 glasses capture shows scene geometry in both eye views. The
1080×2340 phone capture contains the full XREAL controller, APP and Home buttons,
and touchpad. Captures are under Builds/official-hellomr/verification. Wearer
confirmation of stereo fusion and phone controller input is pending. Finish
that check before adding course management to the dev copy.

The isolated dev build now uses the SDK's XR Interaction Hands Setup in a
generated copy of the demo scene and sets InitialInputSource to
ControllerAndHands. The official reference and its installed package were
preserved. Unity built successfully with zero errors. The APK was installed on
the S25 with streamed ADB and launched under the existing ControlGlasses runtime.
The phone capture shows the complete controller and the glasses capture shows
both tracked hand skeletons in both eye views. Pinch selection and phone trigger
interaction still require wearer confirmation before course management.

Artifact: Builds/official-hellomr/ImmerseTheBay-XRI-hands-dev.apk
SHA-256: b3409124672ff76993cbd8e1189e67f8d20eca42a994beb12683bd1678e14e04

For seated input testing, the dev build places a selectable cube 40 cm in front
of the initial head pose and 9 cm below eye height. The S25 build succeeded with
zero errors and the wearer confirmed the cube was visible and within reach.
Contact alone did not produce a selection in the captured Unity log. Left and
right pinch selection and phone trigger selection remain to be verified.

The wearer subsequently confirmed the seated cube turned green for left pinch,
right pinch and phone touchpad selection. The next requested increment is live
video/voice/agent with bilingual dialogue, ahead of course management.

The isolated dev scene adds native Dynamic video, the learning agent and Cartesia
conversation. The video blends into the dim start image, with an additional
transparent fade at the image perimeter. On S25, the previous build rendered
the feathered convenience-store start image in both eyes. Native start
acknowledgement was missing in two runs. A follow-up test build waits for a new
decoded frame when the reply has no type. The glasses were disconnected during
that recheck, so animated video and voice interaction were not verified.

The Unity learning overlay now shows original text with English below each
speaker. The existing portal translation route serves asynchronous requests from
Unity. Clearing the dialogue cancels pending requests. The subtitle build
succeeded with zero errors. Host translation smoke test passed. The S25 saw no
external display during packaging, so the full native video/voice/agent run
requires reconnecting the glasses.

The follow-up subtitle build also plays the scripted clerk reply through
Cartesia WAV, independently of the video command acknowledgements. Live Talk
continues to use the Cartesia conversational stream. Starting speech input or
leaving cancels the scripted reply. The portal now fills in streamed WAV sizes
before Unity decodes it. A real clerk sample decoded at 44.1 kHz, lasted 2.80
seconds, and had nonzero audio. The zero-error Android build was installed on the
S25. APK SHA-256:
4f2ec1585cdede215e7bfb4d6a58650b8c24a5682aeec2e5c13681ba139ab3b3.

On reconnect, the S25 decoded live Dynamic frames. The opening capture shows
the feathered convenience-store scene and bilingual clerk captions in both eyes.
Unity logged clerk audio playback. Capture:
Builds/official-hellomr/verification/bilingual-live/opening.png.
The wearer reported the screen was inverted and behind them. The isolated dev
build now uses the horizontal headset heading and world up, and exposes Center
video on the phone to reposition the screen independently of the session.
This update built with zero errors and was installed. Wearer confirmation of
the new placement, microphone input, conversational reply, and learner
captions is pending.

The updated dev build hides the demo scenery and seated test cube during live
practice, restores them on End, and gives conversation captions more screen
space. On S25 it decoded live Dynamic frames and the portal observed the native
opening agent turn. Android reported an active, unsilenced device microphone
during Cartesia Talk. Unity logged both speakers' translated turns. The
two-speaker capture shows their original text and English in both eye views
with the feathered video edge. The capture angle crops the upper part of the
scene. Wearer confirmation of audio audibility and centered placement is
pending. End cleared the portal session. Build SHA-256
2c053a89011e7c9d0a63e5b7a9bb6d2bd7c6ad7c4ff55ea76a826b83cf59d879.

The next centering build follows headset pitch and heading while using world up
to avoid roll. Its S25 capture shows the entire upright scene in both eyes,
including the top feather edge. Android confirmed active app playback routed to
the Air 2 Ultra USB headset at unmuted volume and active headset microphone
capture during Talk. This repeat yielded a clerk caption but no new learner
caption. The earlier two-speaker capture remains the evidence for the full
caption path. End stopped recording and cleared the portal session.
The installed official package hash exactly matched the preserved release APK.
The centering build SHA-256 is
7373649e9971282d4ca92939d084c57b96d51c2bd3ad831ec710fb8490dd1817.
