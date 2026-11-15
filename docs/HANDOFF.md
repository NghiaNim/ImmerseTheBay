# ImmerseTheBay — Handoff (2026-11-14)

For the current S25 app, Portal setup, and next MR tests, start with the [phone and MR handoff](codex/HANDOFF-phone-mr.md).

Immersive language learning on **XREAL Air 2 Ultra + Samsung Galaxy S25** (no Beam Pro), Unity 6000.0.85f2 LTS, XREAL SDK 3.1.0, with **Visko (Reactor) real-time generated video** as the core experience.

Read next: [INFRASTRUCTURE.md](INFRASTRUCTURE.md) (all modules) · [VISKO.md](VISKO.md) (Reactor integration) · [CAST.md](CAST.md) (glasses view to web) · [../AGENTS.md](../AGENTS.md) (rules for coding agents).

---

## 1. Status at a glance

| Area | State | Verified how |
|---|---|---|
| Repo | All current A–E work integrated and pushed to `main`. Coworker entry: [handoff](codex/HANDOFF-web-dynamic.md). | Git integration history |
| Web lesson | Landscape convenience store, original generated start image, dim peripheral image and feathered central video, compact Japanese overlay, spoken intent mode. Demo: `http://localhost:8791/learn.html`. | Browser checks at 1440×900 / 1280×720; automated speech callback mocked |
| Web speech | Default browser MediaRecorder → server → `gpt-transcribe`; Speak then Stop (15 s cap). Browser speech is optional, with accurate network/permission errors. | Real Chrome recorder with synthetic Chinese mic file → real OpenAI → sandwich detour passed; 1.265 s after Stop. Actual user mic/device still user-tested. |
| Video object pointing | Click “指向物体”, then click an object (hold/release to select). Local image crop → Luna object/bounds → confidence+point gate → existing agent DAG. Real-world mesh labels are separate. | Original store image bottle identified at model confidence 0.95; full route 3.556 s. Repeated small-bottle trials also produced no-hit: localization remains a prototype with false negatives. Person/blank-wall controls produced no hit and no branch. Live-frame object test attempted at 14:53 local; Dynamic returned 429 no available capacity before connection, so this specific path remains unverified. |
| Lesson agent | Forward DAG: temporary interactions rejoin the saved lesson cursor. Arbitrary goals use a server-side planner; strict schema, text scoring, bounded queues and prompt pacing. | Node tests; live status below |
| OpenAI | Key only in ignored `portal/.env`. User requests `OPENAI_MODEL=gpt-6-luna`, `OPENAI_REASONING_EFFORT=none`. Luna access was initially denied, then a fresh retry succeeded after the user updated access/configuration. No automatic older-model substitution. | Real Luna structured plan: 2.784 s in one sample. Full opening → generated bento-heating branch → scored lines → saved-checkpoint rejoin passed in 8.263 s. |
| Visko | Web defaults to **Dynamic**, no silent Stable fallback. Live Dynamic start/image/hint/video succeeded at native 640×368. Live three-node sandwich branch/rejoin commands accepted, video continued, non-silent audio measured (peak 0.273). Stable lacked native and worked at user-approved web-only 1080p. | 30-second Dynamic browser run, always Leave. Deployment omits documented active_prompt/current_prompt: command acceptance is verified, exact applied-prompt telemetry unavailable. |
| Unity learning | Context, overlay, command client, camera, Japanese speech, feather material and scene/menu wiring integrated. New `.meta` files generated. | Unity Android compilation: no `error CS`; LearningContractChecks passed |
| APK | `Builds/ImmerseTheBay.apk` (~230 MB development build); gitignored. | BuildScript.BuildAndroid: Success, 0 errors, 90 seconds |
| Hardware | Air 2 Ultra + Samsung S25 remains untested on device. Glasses retain native resolution; no 1080p fallback. | No adb device attached |
| XREAL SDK | Proprietary 3.1.0 tarball remains gitignored. | APK build |
| Node services | Realtime server tests 5/5. Portal tests 61/61 include agent, DAG, provider and cast; npm run check passed. | node --test |

Video pointing is a 2D pointer rehearsal, not physical hand tracking or 3D hand-to-object distance. Glasses need a calibrated hand-to-video coordinate projection; the vision endpoint accepts normalized frame coordinates.

The original generated convenience-store image is a user-approved exception to the earlier CC0-only sample rule; see `Assets/StreamingAssets/lessons/images/SOURCES.md`. The home lesson remains available. Camera, typography, speech, mesh classification and rendering require glasses verification.


## 2. Hardware change: Samsung S25 instead of Beam Pro
XREAL's docs list "Beam Pro or a supported Android phone — Samsung S24 and later flagship series", so the S25 is a supported host. What changes:
- The **phone is the compute + controller**: glasses connect over USB-C (DisplayPort alt mode). The phone screen shows XREAL's virtual controller (`XREALVirtualController`), so phone-screen taps belong to the controller — don't use raw touches for app actions.
- Enable Developer options + USB debugging on the S25. While the glasses occupy the USB-C port, deploy over **wireless adb**: Settings → Developer options → Wireless debugging, `adb pair <ip:port>`, `adb connect <ip:port>`, then `adb install -r Builds/ImmerseTheBay.apk`.
- Samsung may throttle under sustained load: keep Visko at `native` (already enforced), watch HUD FPS and the XREAL temperature events shown on the HUD.
- Some docs/strings still say "Beam Pro"; replace when touching those files.

## 3. Product direction (agreed): Visko for immersive learning
Why Visko: a real-time, steerable video "world" around the learner is the immersion, and prompts are the control surface, so the experience stays controllable (scene = prompt + optional start image).

**Core loop (MVP)**
1. **Capture what's in front of you** → the photo becomes the Visko start image (`set_image`; 16:9, squashed to 832×480).
2. Visko animates that scene; the app overlays the **target sentences** needed there (e.g. ordering at a café), plus an audio prompt for ambience.
3. **Rehearse**: the learner speaks the line (mic is already captured as PCM chunks), the app checks it (speech-to-text + scoring, to be chosen) and steers the scene with the next prompt, so the "conversation" advances.
4. Repeat per scene; save progress.

**First scene to prepare: Japanese in an everyday Japanese home**
- Requested: a "Doraemon scene" using the 標準日本語 (N1) textbook as material. Two constraints:
  - **Doraemon** is a copyrighted character/brand. Don't put the character, name, art or look into shipped prompts or assets. Use an original setting in that spirit, e.g. "a cozy 1970s Tokyo suburban home, tatami room, sliding doors, warm afternoon light, anime-inspired".
  - The **標準日本語 textbook text is copyrighted**: don't commit or reproduce its dialogues. Either write original N1-level sentences/vocabulary for the scene, or keep the learner's own excerpts local and gitignored (e.g. `Assets/StreamingAssets/lessons/private/`).
- Structure lessons as data so content is swappable. Suggested file: `Assets/StreamingAssets/lessons/ja-home-001.json` → `{ "scenePrompt", "audioPrompt", "startImage", "lines": [{ "jp", "kana", "romaji", "zh", "en", "cue" }] }`.

**Photo capture ("拍眼前照片")**
- Air 2 Ultra's cameras are for tracking. XREAL's RGB camera API (`XREALRGBCameraTexture`, `XREALPhotoCapture`, Camera Features sample) is documented **only for XREAL One + Eye accessory**. So on Air 2 Ultra + S25:
  - **Step 1 (now):** use downloaded sample photos in `Assets/StreamingAssets/lessons/images/` (only images you may redistribute; otherwise keep them gitignored).
  - **Step 2:** capture with the **S25's camera** (Unity `WebCamTexture`, or an Android camera intent via the bridge), then upload to Visko → `set_image`.
  - Reference flow in the official sample: extract `Packages/com.xreal.xr.tar.gz` → `package/Samples~/Camera Features/RGBCameraAndCapture/`, or `Tools > XREAL > Import Official Samples` (imports into `Assets/Samples/XREAL XR Plugin/`, gitignored).
- Bridge work needed: `Assets/Plugins/Android/ViskoReactorBridge.kt` has no upload yet. Add `uploadBytes(ByteBuffer direct, name, mime)` → `FileRef`, then `sendCommand("set_image", "{\"image\": ...}", uploads = ...)` (Android SDK: `reactor.uploadBytes(...)`, and `sendCommand(name, args, uploads)`; check exact `FileRef` JSON with `javap` on the AAR as done in this session). Web portal: file input → `reactor.uploadFile()` → `set_image`.

## 3a. Agent-driven, no manual prompting
Learners never type Visko prompts. A **lesson agent** (LLM, server-side in `portal/`) owns the scene:
- **Inputs it sees each turn:** lesson state (current line, attempts, score), the learner's transcribed speech, the start photo (or a caption of it), and **XREAL context** (below).
- **Outputs it emits** (strict JSON, validated before use): `visko` commands (`set_prompt`, `set_audio_prompt`, `set_image`, `start/pause/resume/reset`), `overlay` (next target line with kana/romaji/translation, hint level), `feedback` (pronunciation/grammar notes), `advance` (next beat / scene done).
- **Loop:** scene start → agent writes the opening prompt from lesson data + photo → learner speaks → STT → agent scores and decides the next prompt (scene reacts to what was said) → repeat. Prompts follow Reactor's prompt guide (1–3 sentences, describe only what changes).
- **Model/keys:** run the LLM call in the portal so keys stay server-side (`ANTHROPIC_API_KEY` in `portal/.env`; default to the latest Claude model). Keep a deterministic fallback (scripted prompts from the lesson file) when the LLM is unavailable.
- **Guardrails:** cap prompt length, never put copyrighted characters/brands into prompts (section 3), rate-limit Visko commands (one prompt per chunk at most), log every agent decision for review.

## 3b. XREAL inputs as assistive signals (feed the agent, not the learner's typing)
| Signal | Source in this repo | How the agent uses it |
|---|---|---|
| Hand gestures | `Tracking/HandInput` (pinch, index tip, palm), XRI interactors | Pinch = "I'm ready / next line"; long pinch = "hint"; point at a scene element (ray from index) = "what is this?" → agent names it in Japanese and weaves it into the next prompt. |
| Spatial mesh + classification | `ARMeshManager`, `Xreal/XrealFeatures` (labels: wall, floor, ceiling, table, door…) | Room context ("learner is next to a table and a door") → agent picks matching vocabulary/situations ("机", "ドアを開けてください") and grounds the scene. |
| Planes | `ARPlaneManager` | Where to place the Visko screen / props; horizontal plane at desk height → "sitting at a table" context. |
| Head pose / gaze | Head device pose, camera forward | What the learner looks at (Visko screen vs. overlay vs. room) → pause/hint when attention drifts; confirm comprehension by gaze on the target word. |
| Anchors | `Anchors/SpatialAnchorController` | Pin a lesson "station" to a real spot (e.g. kitchen = food vocabulary); resuming at that spot resumes that lesson. |
| Mic level / speech | `Audio/MicrophoneCapture` (RMS + PCM chunks) | Voice activity → start/stop STT; silence timeout → hint. |
| Glasses buttons | `XREALCallbackHandler` key events | Hardware "next"/"repeat". |
Package these as a compact JSON **context snapshot** sent with each agent turn (e.g. `{gesture:"pinch", pointedAt:"table", room:["wall","table","door"], gaze:"overlay", speech:"…", rms:0.12}`).

## 3c. Web demo first
Build and validate the whole loop **in the web portal before Unity**:
- `portal/public/learn.html` (+ `learn.js`): pick lesson → upload/choose sample photo → agent starts Visko scene → sentence overlay → browser mic + Web Speech API (or server STT) → agent turn → Visko steers. Simulate XREAL signals with on-page controls (buttons for pinch/hint/point-at, checkboxes for mesh labels) so the agent contract is exercised without glasses.
- Server: `portal/agent/` (lesson loader, agent prompt + JSON schema, Visko command executor via the existing session, STT/TTS adapters), endpoint `POST /api/agent/turn`, events over `/sync` so Unity can later join the same session and mirror the overlay.
- Unity later reuses the same agent endpoint: it only sends the context snapshot + audio and renders overlay/Visko.

## 4. Agreed sequencing
1. **Finish the original build and run the end-to-end test on the S25** (section 5) on `main`.
2. Branch **`feat/immersive-learning`**: web demo first (3c) with the agent loop (3a) and simulated XREAL signals (3b).
3. Then the Unity side: real XREAL signals → context snapshot, overlay UI, photo capture (S25 camera), shared session with the web demo.

## 5. E2E test plan (do this first)
Pre-reqs: `Packages/com.xreal.xr.tar.gz` present; `portal/.env` has `REACTOR_API_KEY`; `Assets/StreamingAssets/visko.json` has the Mac's LAN IP as `portalUrl`; Mac and S25 on the same Wi-Fi.
1. `cd portal && npm install && npm start` (port 8790). Open `http://localhost:8790`.
2. Unity: `Tools > XREAL > Import Official Samples` (first time), `Tools > XREAL > Create Sensor Debug Scene`, `Tools > XREAL > Build APK`; install via wireless adb.
3. Glasses on the S25, launch the app. HUD pages (PgUp/PgDn or quick pinch near the HUD): head pose 6DoF, hands tracked, planes/mesh appear, XREAL device = Air 2 Ultra, Android sensors listed.
4. Menu (poke with index finger): spawn 2D video + 3D model, grab/scale with hands, Save layout, relaunch → items return.
5. Visko: web "New session" (use stable if dynamic is at capacity) → glasses auto-join → same video on "Visko Screen"; Start from web; change prompt; tick "Send commands through Unity" and steer; then glasses "Visko: new session" → web "Join Unity session". **Always Leave** (billing runs while connected).
6. Cast: menu "Cast view on/off" → open `http://localhost:8790/cast.html`.
7. Record results in section 1 and fix failures before branching.

Known risks: joined Reactor clients may not be allowed to send commands (relay path covers it); JNI frame copy cost; XRI grabbing vs our poke menu on the same hand; anchor save gated at 50% coverage (`SaveAll(true)` forces).

## 6. How to run things
| Task | Command |
|---|---|
| Compile check | `/Applications/Unity/Hub/Editor/6000.0.85f2/Unity.app/Contents/MacOS/Unity -batchmode -nographics -quit -projectPath "$PWD" -buildTarget Android -logFile Logs/compile.log; grep "error CS" Logs/compile.log` |
| Regenerate scene | same + `-executeMethod SensorDebugSceneBuilder.Create` |
| Build APK | same + `-executeMethod BuildScript.BuildAndroid` → `Builds/ImmerseTheBay.apk` |
| Portal (Visko + sync + cast) | `cd portal && npm start` |
| Realtime server (not yet in scene) | `cd server && npm start` (port 8787); `npm test` |

Close the Unity editor GUI before batch runs on the same project. Unity Hub must be signed in (Personal license activated).

## 7. Secrets and files never to commit
`portal/.env` (Reactor key) · `Assets/StreamingAssets/visko.json` (LAN IP; template `visko.example.json`) · `Packages/com.xreal.xr.tar.gz` · `Assets/Samples/XREAL XR Plugin/` · `Library/`, `Logs/`, `Builds/`, `*.log`. Codex CLI writes `error.log` containing account ids into its working directory; it is gitignored, keep it that way.

## 8. Parallel work with Codex (local)
- Briefs `docs/codex/01–06` are done and merged. Agent rules: `AGENTS.md`.
- Launch pattern that works with codex-cli 0.166 (this version has no `--full-auto`):
  `codex exec -C <worktree> -s workspace-write --add-dir <repo>/.git --skip-git-repo-check -o <log>.final.md "<prompt>"`
- The sandbox **cannot write the worktree's git index**, so agents can't commit. The coordinator reviews, commits for them, compiles in Unity (agents can't run Unity), then merges.
- Old worktrees in `/Volumes/Mac_Working/GitHub/ImmerseTheBay-worktrees/` are merged; remove with `git worktree remove <path>`. Their commits predate the LFS rewrite (different hashes from `main`) — don't merge them again.
- Not verified: whether `codex exec` runs show up in the Codex app UI.

## 9. Open follow-ups (prioritised)
Codex prompt for the next phase (parallel workstreams A–E): [codex/PROMPT-immersive-learning.md](codex/PROMPT-immersive-learning.md).

1. E2E on the S25 (section 5) and fix findings.
2. `feat/immersive-learning`: lesson JSON + first Japanese home scene (original content), photo → `set_image` (sample images first, then S25 camera), sentence overlay, mic rehearse + scoring (choose STT: on-device Android `SpeechRecognizer` vs a cloud API), web portal image upload.
3. Visko: per-scene `set_audio_prompt`; verify adopted-client command rights; retest dynamic model capacity.
4. Place `RealtimeSession` in the scene if multi-user rooms are needed (server is ready).
5. Replace remaining "Beam Pro" wording; smaller release build (strip glTF shader variants if 3D models aren't needed).

## 10. References
**XREAL**
- SDK docs (3.1.0): https://docs.xreal.com/ — Getting Started https://docs.xreal.com/Getting%20Started%20with%20XREAL%20SDK · Camera https://docs.xreal.com/Camera/Camera · RGB camera https://docs.xreal.com/Camera/Access%20RGB%20Camera · Input https://docs.xreal.com/category/input-and-interactions · Controller https://docs.xreal.com/Input%20and%20Interactions/Controller · Hand tracking https://docs.xreal.com/Input%20and%20Interactions/Hand%20Tracking · Plane detection https://docs.xreal.com/Plane%20Detection · Meshing https://docs.xreal.com/Depth%20Mesh/NormalMesh · Mesh classification https://docs.xreal.com/Depth%20Mesh/MeshClassification · Image tracking https://docs.xreal.com/Image%20Tracking/intro · Spatial anchors https://docs.xreal.com/Spatial%20Anchor/intro , https://docs.xreal.com/Spatial%20Anchor/Anchors
- SDK download (accept ToS): https://developer.xreal.com/download · API reference: https://developer.xreal.com/reference/nrsdk/overview · GitHub: https://github.com/nreal-ai
- SDK source facts used (in `Packages/com.xreal.xr.tar.gz`): `Runtime/Scripts/XREALPlugin.cs` (input source, tracking type, device info), `XREALCallbackHandler` (glasses events), `Runtime/Scripts/Android/AR Features/XREALAnchorSubsystem*.cs` (ARF 6 persistent anchors, no descriptor flags), `XREALImageTrackingSubsystem.cs` (`supportsMutableLibrary=false`), `XREALMeshSubsystemExtensions` (`GetFaceClassifications`), `Editor/Android/XREALBuildProcessor.cs`, `Runtime/Prefabs/XR Interaction Hands Setup.prefab`, samples under `Samples~/`.

**Visko / Reactor**
- Visko: https://www.visko.ai (models page https://www.visko.ai/models)
- Model docs: stable https://docs.reactor.inc/model-api-reference/visko-orbis-stable/overview (schema/tutorial/prompt-guide: same path + `/schema.md`, `/tutorial.md`, `/prompt-guide.md`) · dynamic https://docs.reactor.inc/model-api-reference/visko-orbis-dynamic/overview (same sub-pages)
- Platform: auth https://docs.reactor.inc/authentication.md · quickstart https://docs.reactor.inc/quickstart.md · sessions (multi-client join) https://docs.reactor.inc/concepts/sessions.md · tracks https://docs.reactor.inc/concepts/tracks.md · JS SDK https://docs.reactor.inc/sdk-reference/reactor-class.md · Android SDK https://docs.reactor.inc/sdk-reference/android/installation.md , `/reactor.md`, `/track.md`, `/types.md` · rate limits https://docs.reactor.inc/resources/rate-limits.md · billing https://docs.reactor.inc/resources/billing.md · pricing JSON https://api.reactor.inc/pricing · docs index https://docs.reactor.inc/llms.txt · dashboard (API keys) https://reactor.inc/dashboard · status https://status.reactor.inc
- Packages: npm `@reactor-team/js-sdk@3.4.0` (loaded via https://esm.sh/@reactor-team/js-sdk@3.4.0), Maven `inc.reactor:reactor-sdk-android:1.1.0` (https://repo1.maven.org/maven2/inc/reactor/reactor-sdk-android/1.1.0/), javadoc https://javadoc.io/doc/inc.reactor/reactor-sdk-android/1.1.0

**Unity / packages**
- AR Foundation 6.1: https://docs.unity3d.com/Packages/com.unity.xr.arfoundation@6.1/manual/index.html · XR Interaction Toolkit 3.0: https://docs.unity3d.com/Packages/com.unity.xr.interaction.toolkit@3.0/manual/index.html · XR Hands 1.5: https://docs.unity3d.com/Packages/com.unity.xr.hands@1.5/manual/index.html · XR Plug-in Management: https://docs.unity3d.com/Packages/com.unity.xr.management@4.5/manual/index.html · glTFast: https://docs.unity3d.com/Packages/com.unity.cloud.gltfast@6.8/manual/index.html · Android Gradle templates: https://docs.unity3d.com/6000.0/Documentation/Manual/gradle-templates.html · Unity CLI / Hub: https://docs.unity3d.com/hub/unity-cli · Unity release API used to pick LTS: https://services.api.unity.com/unity/editor/release/v1/releases
- Sample assets: Khronos glTF samples https://github.com/KhronosGroup/glTF-Sample-Assets · sample video https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/BigBuckBunny.mp4

**Tools**
- Codex CLI (`codex exec`, `codex cloud`) — local version 0.166.0. Repo: https://github.com/NghiaNim/ImmerseTheBay

