# Codex prompt — Immersive learning (agent-driven Visko), parallel workstreams

Copy everything in the block below into Codex (run from the repo root `/Volumes/Mac_Working/GitHub/ImmerseTheBay`).

```text
You are coordinating parallel coding agents on the ImmerseTheBay repo (Unity 6000.0.85f2 LTS + XREAL Air 2 Ultra on a Samsung S25, Visko/Reactor real-time video, Node web portal).

READ FIRST, in order: AGENTS.md, docs/HANDOFF.md (especially sections 3, 3a, 3b, 3c, 4, 7, 10), docs/VISKO.md, docs/INFRASTRUCTURE.md. Section 10 of HANDOFF.md lists every external reference; use those docs, and verify APIs against package source/AAR before using them.

GOAL: an agent-driven immersive Japanese-learning loop. The learner never types Visko prompts: a server-side lesson agent writes all prompts from lesson data, the learner's speech, the start photo and XREAL context signals. Build and validate it in the WEB PORTAL FIRST, then the Unity side.

SETUP
- Base branch: create `feat/immersive-learning` from `main`. Each workstream below works on its own branch `feat/il-<letter>-<name>` from `feat/immersive-learning`, ideally in its own git worktree (`git worktree add ../ImmerseTheBay-il-<letter> -b feat/il-<letter>-<name> feat/immersive-learning`). Copy `Packages/com.xreal.xr.tar.gz`, `portal/.env` and `Assets/StreamingAssets/visko.json` into each worktree if needed (all gitignored; never commit them).
- If you can run several agents at once, run one agent per workstream A–E in parallel. Otherwise do them in the order A, C, B, D, E.
- Stay strictly inside each workstream's owned paths. Anything needed elsewhere goes into a "Requests for integration" list in the final summary.
- Do not launch the Unity editor inside a sandbox. If Unity is available outside the sandbox, compile-check with the command in AGENTS.md; otherwise keep C# compile-safe by matching APIs already used in the repo or verified in package source, and say so.
- Commit in small logical commits, imperative messages, no AI/tool attribution. If git cannot write the index (sandbox), leave changes uncommitted and report it.
- Never commit secrets, the XREAL SDK/samples, copyrighted textbook text, or images you can't redistribute. Never put copyrighted characters/brands (e.g. Doraemon) into prompts or assets — use original settings "in that spirit".

SHARED CONTRACT (all workstreams implement against this; put it in `portal/agent/contract.md` in workstream A and treat it as source of truth)
- POST /api/agent/turn  request: { sessionId, lessonId, beat, transcript?, imageRef?, context: { gesture?: "pinch"|"long_pinch"|"point"|null, pointedAt?: string|null, roomLabels?: string[], planes?: {horizontalAtDeskHeight:boolean}, gaze?: "visko"|"overlay"|"room", rms?: number, glassesKey?: string|null } }
- response: { visko: [{ name, data }], overlay: { jp, kana, romaji, zh, en, hintLevel } | null, feedback: { score: 0-100, notes: string[] } | null, advance: "stay"|"next_line"|"scene_done" }
- Lesson file: Assets/StreamingAssets/lessons/<id>.json = { id, title, level, scenePrompt, audioPrompt, startImage, beats: [{ goal, lines: [{ jp, kana, romaji, zh, en, cue }], vocab: [{ jp, kana, en, objectHint }] }] }
- Visko rules: always `set_resolution` "native" before `start`; prompts 1–3 sentences, describe only what changes (Reactor prompt guide); at most one `set_prompt` per ~2 s chunk.

WORKSTREAMS

A. Lesson agent service — owns `portal/agent/**`, plus small additive wiring in `portal/server.js` (register routes only).
   - Implement `POST /api/agent/turn` per the contract: load lesson, build the LLM prompt (lesson beat, transcript, context snapshot, photo caption), call the LLM server-side (ANTHROPIC_API_KEY in portal/.env; default to the latest Claude model; read the official Anthropic API docs), validate strict JSON output against a schema, clamp/repair, rate-limit Visko commands.
   - Deterministic fallback when no LLM key or on error: scripted prompts/lines from the lesson file.
   - Scoring v1: normalized kana comparison of transcript vs target line (+ LLM notes when available).
   - Guardrails: prompt length cap, banned-terms list for copyrighted characters/brands, log every decision (JSONL under portal/agent/logs/, gitignored).
   - Tests with `node --test` using a mocked LLM (no network). `npm test` in portal must pass.

B. Web demo — owns `portal/public/learn.html`, `portal/public/learn.js`, `portal/public/learn.css` (new files; add one link from index.html only as a request).
   - Flow: pick lesson → choose/upload photo → create or join the Visko session (reuse the approach in public/app.js: token from /api/token, @reactor-team/js-sdk@3.4.0) → `uploadFile` + `set_image` → call /api/agent/turn → execute returned Visko commands on the session → show overlay (jp/kana/romaji/zh/en) → browser mic with Web Speech API (ja-JP) → next agent turn. No prompt text box for the learner.
   - Simulated XREAL signals panel: buttons Pinch / Long pinch / Point-at <object>, checkboxes for room labels (wall, floor, table, door, ceiling), gaze selector, RMS slider. They fill `context` exactly like the glasses will.
   - Broadcast overlay + agent decisions on /sync (`{type:"learn", ...}`) so Unity can mirror them later.
   - Always Leave the Reactor session on page unload; show billing warning.

C. Lesson content — owns `Assets/StreamingAssets/lessons/**`, `docs/LESSONS.md`.
   - Write `ja-home-001.json`: an ORIGINAL everyday Japanese home scene (cozy 1970s Tokyo suburban home, tatami room, anime-inspired, NO copyrighted characters), 4–6 beats, N1-level but natural spoken lines, with kana, romaji, zh, en, cues, and vocab with `objectHint` matching mesh labels (table, door, wall, floor...). Do not copy any textbook text.
   - `docs/LESSONS.md`: schema, authoring rules (copyright, prompt-guide style), how private local excerpts go in `lessons/private/` (gitignored — add the ignore rule as a request).
   - 2–3 sample start images: only CC0/public-domain sources, record source URL + license in `lessons/images/SOURCES.md`; if unsure, don't commit the image, just list the URL.

D. Unity learning client — owns `Assets/Scripts/Learning/**` (new).
   - `LearningContext`: builds the context snapshot from existing modules — `Tracking/HandInput` (pinch, long pinch ≥0.6 s, pointing ray from index tip → raycast against mesh/planes → nearest classified label), `Xreal/XrealFeatures` mesh label counts / per-hit labels (XRMeshSubsystem.GetFaceClassifications), `ARPlaneManager` (horizontal plane ~0.6–0.9 m below head), gaze (camera forward vs Visko screen/overlay bounds), `Audio/MicrophoneCapture` RMS + VAD, XREAL glasses key events.
   - `LearningAgentClient`: POSTs /api/agent/turn to the portal (URL from visko.json `portalUrl`), applies the response: forwards Visko commands through `Visko/ViskoSession.Command(...)`, renders the overlay (world-space panel under the Visko screen, legacy UI Text like SensorDataHud, Japanese-capable font — request a font asset if needed), shows feedback.
   - STT on device: Android `SpeechRecognizer` (ja-JP) via a small Java/Kotlin helper in `Assets/Scripts/Learning/` is NOT allowed (Plugins path is E's); instead define an `ISpeechToText` interface + editor stub here and request the Android implementation from E.
   - Mirror web overlay events from /sync when the web demo drives the session.
   - HUD lines via DebugRegistry.

E. Android camera + Visko image upload — owns `Assets/Plugins/Android/**`, `Assets/Scripts/Visko/**`.
   - Extend `ViskoReactorBridge.kt`: `uploadImage(bytes: ByteArray, name, mime)` using Reactor Android SDK `uploadBytes(direct ByteBuffer, name, mime)` → FileRef, then `sendCommand("set_image", <args>, uploads=<FileRef json>)`. Verify exact FileRef/`uploads` format by inspecting the AAR (`javap` on classes.jar from https://repo1.maven.org/maven2/inc/reactor/reactor-sdk-android/1.1.0/) — don't guess.
   - `ViskoSession.SetImage(byte[] jpg)` + `SetImageFromStreamingAssets(path)` (sample photos from lessons/images first).
   - Photo capture on the S25: `PhotoCapture` using the phone camera (Unity `WebCamTexture` first; Android camera intent via the bridge if better quality is needed), JPEG 16:9. Note: XREAL RGB camera API is One+Eye only; Air 2 Ultra can't provide RGB frames (see HANDOFF §3).
   - Android `SpeechRecognizer` (ja-JP) helper in Kotlin implementing D's `ISpeechToText` contract (expose start/stop + partial/final result callbacks to C# via AndroidJavaProxy, like ViskoReactorBridge.Listener).
   - Keep Gradle templates working (AGP 9 built-in Kotlin, `android.uniquePackageNames=false`).

DONE WHEN
- Web demo (A+B+C) runs locally: `cd portal && npm install && npm test && npm start`, open /learn.html, choose ja-home-001 + a sample photo, the agent starts the Visko scene with no typed prompt, speaking a line advances the scene, simulated pinch/point/room labels visibly change the agent's next prompt or overlay. Use the stable model if dynamic returns 429. Keep any live Reactor test under 2 minutes and Leave the session.
- Unity (D+E): no `error CS` (if Unity available), APK builds with `-executeMethod BuildScript.BuildAndroid`.
- Each workstream ends with a summary: what was built, files changed, how it was verified, what was not verified, requests for integration. Then the coordinator merges A→C→B→E→D into feat/immersive-learning, resolves requests (server.js routes, index.html link, .gitignore rules, scene builder wiring of Learning components), runs all tests and a final build, and updates docs/HANDOFF.md section 1.
```
