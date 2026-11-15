# Home and Real world practice — Web handoff

Issue: https://github.com/NghiaNim/ImmerseTheBay/issues/11

Run the portal from `portal/` with `PORT=8793 npm start`, then open http://localhost:8793/learn.html. Keys remain in ignored `portal/.env`. This slice adds Web modes/review; it does not rebuild the Unity APK.

## Demo flow

1. **Home** selects an authored scenario. **Real world** accepts a JPEG/PNG/WebP up to 3 MiB and a spoken or typed goal, then creates a photo-grounded 2–4-line Japanese practice.
2. Start practice. Romaji is visible with the Japanese target. Conversation uses Cartesia for separate partner speech, interruptions, goal changes and return to the saved lesson. Dynamic generates environmental audio only.
3. Use **End practice**, or complete the last target, to disconnect and show the review. **Leave** remains an immediate exit without review.
4. Review shows encountered words, attempted/completed targets, transcript match, first-attempt success, hints and skips. No scored attempts means no percentage. Transcript similarity is not pronunciation assessment; intent changes and skips are not scored answers.

The voice agent receives server-owned scene/goal/current-target context. Speaking the current target is rehearsal; a new goal branches. A photo without a visible partner keeps an environment-only video prompt while the conversation occurs off-camera. Existing visible partners receive small lip-motion instructions, without strict lip sync.

## Verification (2026-11-14 local)

- Portal: 163 tests passed; `npm run check` passed.
- Real photo + synthesized Chinese microphone → real OpenAI STT → real generated practice: 3 lines. Simulated rehearsal recognition then exercised actual scoring and automatic review: 4 attempts, 3 completed targets, first-attempt success 2/3, 4 vocabulary items. Home skip-only review correctly showed no score.
- Actual empty-home photo + Chinese goal generated 3 lines in 6.0 seconds, `visiblePartner: false`. Current-code agent produced the environment-only prompt without person or mouth cues.
- Real Cartesia + real Dynamic: 42.2 seconds; two provider-confirmed interrupted assistant turns; main → sandwich → water → rejoin. Video advanced from 0.02 to 26.88 seconds at 640×368. Microphone input was synthesized Japanese audio, not a human/device acoustics test.
- Delayed Home response after Leave → Real world could not overwrite photo/text or enable Begin. No provider calls in this regression.
- Real Cartesia rehearsal in preview mode passed in 18.6 seconds: synthesized target speech → managed recognition → `rehearse_line`, with no branch. Review recorded one attempt, 94% transcript match, one completed target and 1/1 first-attempt success. No page errors; microphone tracks ended on exit. `portal/agent/e2e-voice-rehearsal.cjs` generates its own local audio fixture.
- Screenshots were inspected. Real video still showed camera/framing drift. Mouth movement, face consistency and exact visual application of a steering prompt remain model-dependent; command acceptance is not proof of lip synchronization.

Regression scripts: `portal/agent/e2e-modes.cjs`, `e2e-mode-race.cjs`, `e2e-voice.cjs --live`. Set `PLAYWRIGHT_MODULE` to an installed Playwright package and `CHROME_PATH` if its bundled Chromium is absent. Modes E2E expects a spoken-goal WAV at `/tmp/il-modes-e2e/goal.wav`; voice E2E expects sandwich/bottle/return WAV fixtures under `/tmp/il-voice-e2e/`. These local synthetic audio fixtures and screenshots are not committed.

## Operational limits

- Chinese spoken **setup goals** use the separate server STT path. Managed live conversation supports Japanese/English, not Chinese microphone recognition.
- Generated lessons and original photo bytes stay in server memory for at most 30 minutes, capped at 32 entries. Restart/expiry/eviction invalidates new turns for that practice. Derived scene descriptions and transcripts still use the existing decision log; do not describe all derived data as ephemeral.
- Photo generation uses configured OpenAI Luna, strict schema, 30-second timeout, two concurrent calls and five-second caller cooldown. No new dependencies.
- Cartesia's dedicated deployed agent was updated with the scene-aware instructions and dynamic opening. Reprovisioning still requires the normal explicit create path; do not create duplicate agents.
- Future work: Unity mode/review parity, real microphone/device testing, and stronger video stabilization/identity control.
