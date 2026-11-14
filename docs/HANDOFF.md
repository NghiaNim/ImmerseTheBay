# ImmerseTheBay — Handoff (2026-11-14)

Immersive language learning on **XREAL Air 2 Ultra + Samsung Galaxy S25** (no Beam Pro), Unity 6000.0.85f2 LTS, XREAL SDK 3.1.0, with **Visko (Reactor) real-time generated video** as the core experience.

Read next: [INFRASTRUCTURE.md](INFRASTRUCTURE.md) (all modules) · [VISKO.md](VISKO.md) (Reactor integration) · [CAST.md](CAST.md) (glasses view to web) · [../AGENTS.md](../AGENTS.md) (rules for coding agents).

---

## 1. Status at a glance

| Area | State | Verified how |
|---|---|---|
| Repo | `main` pushed to github.com/NghiaNim/ImmerseTheBay (public), head `95d3ca1`. Git LFS is **disabled** on the repo; LFS rules were removed — commit small binaries normally. | `git push` |
| Unity project | Compiles with 0 errors; debug scene generated on XREAL's official rig; APK builds (`Builds/ImmerseTheBay.apk`, ~220 MB dev build). | Unity batch compile + `BuildScript.BuildAndroid` |
| XREAL SDK | 3.1.0 tarball in `Packages/` (**gitignored**, proprietary). Loader auto-enabled for Android, `XREALSettings` registered, input source Hands, tracking 6DoF. | Batch build |
| Visko web portal | Works end to end. Stable model: session ready in ~2 s, live 18 fps video + audio, prompt steering morphs the scene. Dynamic model returned **429 no capacity** at test time (Reactor-side). Native 832×480 is now forced before every start (not re-tested live). | Real run in browser, ~1 min (~$0.6) |
| Visko in glasses | Kotlin bridge + Reactor Android SDK compile into the APK (`libreactor_*.so` present). **Never run on a device.** | APK inspection |
| Everything else on device | **Never run on hardware** (hands, anchors, mesh, cast, media, mic, XRI grabbing). | — |
| Node services | `server/` tests 5/5, `portal/cast` tests 4/4. Portal `/sync` + `/cast` share one HTTP server. | `npm test`, `node --test` |

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

## 4. Agreed sequencing
1. **Finish the original build and run the end-to-end test on the S25** (section 5) on `main`.
2. Then create branch **`feat/immersive-learning`** for the MVP in section 3.

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
1. E2E on the S25 (section 5) and fix findings.
2. `feat/immersive-learning`: lesson JSON + first Japanese home scene (original content), photo → `set_image` (sample images first, then S25 camera), sentence overlay, mic rehearse + scoring (choose STT: on-device Android `SpeechRecognizer` vs a cloud API), web portal image upload.
3. Visko: per-scene `set_audio_prompt`; verify adopted-client command rights; retest dynamic model capacity.
4. Place `RealtimeSession` in the scene if multi-user rooms are needed (server is ready).
5. Replace remaining "Beam Pro" wording; smaller release build (strip glTF shader variants if 3D models aren't needed).
