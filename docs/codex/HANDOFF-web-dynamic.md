# Coworker handoff — immersive learning web and Unity

Updated 2026-11-14. All current A–E work is integrated into `main`; feature implementation head was `77d115c`. Start from `main`, not individual workstream branches. Read `AGENTS.md`, `docs/HANDOFF.md`, and `docs/INFRASTRUCTURE.md` first.

## Run the web experience

```sh
git pull --ff-only origin main
cd portal
npm ci
# Create .env from .env.example; populate keys locally.
PORT=8791 npm start
```

Open http://localhost:8791/learn.html. Live Dynamic is the default. Practice preview is an explicit static-image mode with no generated video. OpenAI credentials remain server-side in the ignored `portal/.env`; they are not included in Git. Configure:

```dotenv
REACTOR_API_KEY=<your Reactor key>
OPENAI_API_KEY=<your OpenAI key>
OPENAI_MODEL=gpt-6-luna
OPENAI_REASONING_EFFORT=none
OPENAI_TRANSCRIBE_MODEL=gpt-transcribe
```

Optional `PORTAL_TOKEN` protects the existing API routes. Use localhost or HTTPS for microphone access. Restart the server after changing environment variables. A fresh server loses in-memory lesson state: Leave and start a new lesson.

## What works

- Landscape convenience-store experience: original generated starting image, dim peripheral environment, feathered central video, compact Japanese overlays.
- Speak → Stop records at most 15 seconds, then sends audio to server-side OpenAI transcription. Choose “自由说 · Tell guide” for spontaneous Chinese/English/Japanese goals. Browser SpeechRecognition is optional; its network error is separate from microphone permission.
- Server-owned DAG detours preserve the pending main lesson cursor and rejoin it. Luna handles arbitrary goals; known sandwich/product/counter interactions have deterministic paths. Scoring is text similarity, not acoustic pronunciation assessment.
- “指向物体” enables pointer rehearsal: click to inspect; hold/release to select. A local crop of the displayed image/video is grounded by Luna, validated against confidence and point/bounds, then sent as a learner intent through the same DAG.
- New Dynamic sessions retry capacity/rate-limit 429s: four attempts, 90-second budget, jittered backoff, provider Retry-After, visible countdown, Leave cancellation, and page cooldown. Allocated sessions and existing joins are not blindly recreated. No automatic Stable fallback.
- Unity camera, Japanese Android speech, learning client/overlay/context, scene/menu wiring, mesh collider and feather material are integrated. All new Unity asset metadata is committed.

## Verified evidence

- Portal: 61 tests passed; `npm run check` passed. Realtime `server/`: 5 tests passed.
- Actual Luna structured detour: bento heating, scored progression, return to saved main checkpoint. One plan took 2.784 seconds; not a latency guarantee.
- Real Chrome MediaRecorder with synthetic Chinese microphone audio → real gpt-transcribe → sandwich detour: passed, about 1.3 seconds after Stop.
- Real Dynamic: image upload, generated video and non-silent audio, accepted sandwich branch commands, and return-to-main commands. Always Leave after bounded tests.
- Starting-photo bottle localization → real Luna → detour: passed once at reported confidence 0.95, about 3.6 seconds total. Person/blank-wall controls did not trigger branches. Repeated tiny-bottle trials also missed: accuracy is not production-validated.
- Unity 6000.0.85f2 Android compilation and LearningContractChecks passed. APK built successfully with zero errors (~230 MB actual file). APK is local under Builds/ and not committed. No S25/Air 2 Ultra hardware validation.

## Immediate next task

Finish the user-requested real-video test: point at a visible bottle, verify the recognized object and generated prompt, observe the video action, finish the detour, and confirm the original main line returns. This FULL chain has NOT yet passed on a live frame. Previous attempts hit Reactor 429 capacity; latest manual browser tabs disappeared during testing. Do not describe the static-photo pass as a live-frame pass.

The current Reactor deployment emits prompt_accepted and frame/audio metrics but omits the documented active_prompt/current_prompt fields. The debug trace keeps acceptedPrompt distinct from applied-prompt telemetry. Acceptance alone does not prove visual adoption; inspect successive frames/video.

Pointer grounding is 2D, not physical hand tracking. Glasses still need calibrated hand-to-video coordinate projection; true 3D hand/object distance cannot be inferred from this prototype. Keep native resolution on glasses; the approved 1080p fallback applies only to Web when native is unavailable.

## Entry points

- `portal/public/learn.js`, `scene-point.js`, `dynamic-retry.js`: web flow, pointing, capacity handling.
- `portal/agent/contract.md`, `service.js`, `flow.js`, `transcribe.js`: lesson, DAG and speech API.
- `portal/vision/README.md`: grounding contract and limitations.
- `Assets/Scripts/Learning/README.md`: Unity wiring and device checks.
- `docs/VISKO.md`: video integration and retry policy.

Do not commit .env, local transcripts/logs, Builds/, or the proprietary XREAL SDK. Old worktrees under ImmerseTheBay-worktrees predate the LFS rewrite; do not merge them again.

## Character continuity/context follow-up (2026-11-14)

See [implementation and verification report](IMPLEMENTATION-character-context.md), tracked in issue #1. Visual prompts now receive final server-owned character and scene anchors; reference/caption are pinned per run. Both model adapters use bounded allowlisted context, with completed branch memory retired before rejoin refinement. All 80 portal tests and syntax checks pass. Real Dynamic transport/progression was exercised; facial identity is a separate acceptance gate and is not guaranteed by prompt text. The report records live findings and reproduction commands.

Cartesia now handles clerk dialogue and learner Listen playback; Visko audio prompts are environmental-only. Haruka is configured in the ignored env and the example contains the public voice ID. Full integrated portal suite: 100/100 passed. See [Cartesia report](IMPLEMENTATION-cartesia.md), issue #2, for actual browser/audio evidence and limitations.
