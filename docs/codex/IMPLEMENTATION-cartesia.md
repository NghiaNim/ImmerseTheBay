# Cartesia dialogue and environmental video audio

Tracking: https://github.com/NghiaNim/ImmerseTheBay/issues/2

## User story and plan

The convenience-store clerk speaks through Cartesia. Learners can also listen to their own practice sentence. Visko produces environmental audio only, with explicit no-speech/no-dialogue/no-narration constraints.

1. Keep `CARTESIA_API_KEY`, voice, model and API version in ignored server `portal/.env`.
2. Select a Japanese customer-service voice from the official catalog and verify real synthesis.
3. Add an authenticated server WAV endpoint with bounded text, concurrency, response size, timeout, cancellation and redacted errors.
4. Keep clerk dialogue separate from the learner overlay. Play clerk lines after successful scene decisions; provide learner Listen and clerk replay controls.
5. Cancel stale audio on next turn, microphone start and Leave. Duck environmental audio while speech plays and restore its prior level.
6. Enforce environmental-only Visko audio after model refinement and cover both normal and fallback prompts.
7. Verify HTTP boundaries, playback, cancellation, role separation and a bounded real Dynamic + Cartesia browser run.

## Voice and provider contract

Selected **Haruka**, `861213b7-f057-45c8-9527-0f4c144f1a03`, a public Japanese voice described as clear and polite for customer assistance. The authenticated voices API returned this ID, and a real welcome-line synthesis returned HTTP 200 with a 366,990-byte WAV. Selection is based on catalog suitability; no claim of comparative listening across all voices.

- [Official Japanese voice catalog](https://www.cartesia.ai/voices/library?language=ja)
- [Official bytes endpoint](https://docs.cartesia.ai/api-reference/tts/bytes)

Server configuration: `CARTESIA_API_KEY`, `CARTESIA_VOICE_ID`, `CARTESIA_MODEL=sonic-latest`, `CARTESIA_API_VERSION=2026-08-14`. No client key or new package dependency.

`POST /api/agent/speak` accepts `{text, language}` with 1–1,000 characters and `ja`, `en` or `zh`. It returns `audio/wav`, `Cache-Control: no-store`. The server owns the voice; clients cannot override credentials, model or voice. Two concurrent synthesis slots, 15-second deadline and 8 MiB audio cap bound work.

Agent responses may include `speech: {role: "clerk", text, language: "ja"}` or null. The existing learner overlay remains the customer's line. Clerk lines are short original deterministic cues selected for the current action, not additional model calls, inventory assertions or transaction confirmations. Arbitrary branch actions may receive a generic acknowledgment; richer authored dialogue is a future content improvement. Repeated hints do not automatically repeat the clerk.

## Verification

Full portal suite: 100/100 passed; syntax checks passed. Practice browser-to-real-API test completed five lesson turns in 20.3 seconds; real clerk and learner WAV playback returned HTTP 200, and volume duck/restore assertions passed. Combined real Dynamic + Cartesia run passed the five-turn transport/playback/progression checks in 48.2 seconds, with four successful WAV responses and confirmed Leave. Evidence: `/tmp/il-cartesia-live/live-report.json` and opening/detour/rejoin screenshots. Rapid Next actions intentionally canceled superseded speech. Use `portal/agent/e2e-browser.cjs --speech` for actual Cartesia playback through the browser and server, and add `--live` for Dynamic video. Speech-recognition input in this runner is simulated; synthesized audio is real. A prompt prohibiting speech does not guarantee the video model never emits a voice, and this integration does not provide lip synchronization or audio-source separation.

The video keeps its existing mute/unmute control. During Cartesia speech the environmental track is ducked; it is not destructively stripped or replaced.

### Visual limits observed in the combined run

Frame review still showed an added foreground customer, camera/layout drift and changed visible character/clothing by rejoin despite the negative prompt. Therefore face/camera continuity remains **not passed**, tracked in issue #1. The browser test establishes media playback and lesson progression, not exact adoption of every visual cue. Environmental-only prompts were asserted, but no isolated audio analysis established complete absence of generated voice; no perfect voice suppression or lip-sync claim is made.
