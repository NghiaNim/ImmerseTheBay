# Character continuity and bounded context implementation

Tracking: https://github.com/NghiaNim/ImmerseTheBay/issues/1

## User story

Start a convenience-store lesson, request a spontaneous shopping detour, and return to the saved lesson with the same clerk and a bounded, relevant model context.

## Plan

1. Compose every outgoing visual prompt after model refinement with an immutable identity constraint. Reserve its space inside the existing 600-character, 95-word, three-sentence limit. Keep audio prompts separate; the Cartesia follow-up now constrains video audio to ambience only.
2. Pin the starting reference per lesson run. Reject silent same-run reference changes; reset explicitly creates a new anchor.
3. Use a shared allowlisted model-context builder for both adapters. Include current goal, line, return checkpoint and at most three issued-action summaries; never label commands as visually confirmed.
4. Bound serialized context, prune optional history first, and fail safely if required context cannot fit. Keep transcripts and sensing data turn-local.
5. Test prompt paths, provider fallback, conflicting identity edits, reference lifecycle and long-session bounds. Run browser-to-real-API opening, detour and rejoin checks, then a bounded real Dynamic video trial with cleanup.

## Parallel ownership

- Continuity worker: `portal/agent/continuity.js`, its tests, and `prompts.js`.
- Context worker: `portal/agent/model-context.js`, its tests, and model adapters.
- Coordinator: service integration, integration tests, English issue/report, browser and live validation.

## Acceptance and limits

- All scripted/refined/detour/rejoin visual prompts include the same identity constraint without exceeding limits.
- Reference changes require reset. The model receives bounded relevant state, not cumulative DAGs or transcripts.
- Browser UI, API responses and rendered lesson progression agree on opening, detour and saved-checkpoint rejoin.
- Actual video evidence is required to assess facial continuity. Capacity failures, blurry faces and command acknowledgments are not visual passes.
- This slice does not implement the separate v2 receipt/replay protocol, shared driver lease, cancel/replace semantics, object tracking or hardware calibration. It preserves the existing API and model configuration.

## Verification results

Implementation is complete for this slice. Both providers share the context builder. The service pins reference/caption per run, rejects replacement with HTTP 409, and adds continuity anchors after refinement. A completed detour retires its action memory; an advancing turn updates the checkpoint before model refinement. Context has a hard 12,000-byte UTF-8 ceiling (not a token guarantee), with at most three distinct recent issued-action summaries.

| Boundary | Result | Evidence |
|---|---|---|
| Unit/integration | 80/80 tests pass; syntax checks pass | Reference lifecycle, explicit identity mutations, model failures, length limits, long sessions, branch retirement and checkpoint consistency covered |
| Browser → real API → UI | Pass | Five turns: main, detour, detour, detour, rejoin; returned Japanese line equals saved line; every visual command contains anchor; 12.1 seconds |
| Real Dynamic media + progression, first trial | Pass for transport/progression | Five turns in 44.0 seconds; video dimensions/non-paused state/advancing media time asserted at opening, detour and rejoin; Leave completed |
| First trial visual identity | Not passed | Frame review showed facial drift versus the starting photograph and an extra customer; same outfit alone is insufficient |
| Revised prompt trial | Transport/progression pass; visual gate open | First attempt timed out on `set_prompt` and left cleanly. A fresh session completed five turns in 44.1 seconds and left cleanly; reviewed frames had no added customer, but the camera progressively moved into a close-up. This single sample does not establish exact face preservation. |

The E2E speech callback is simulated; it exercises the actual browser turn handler, API, configured model and Dynamic transport. It does not verify microphone/STT accuracy. Example context-size comparison on the opening planner fixture, excluding new scene memory: previous serialization 2,603 bytes; allowlisted serialization 1,144 bytes. This is a fixture comparison, not a production token or latency benchmark.

### Reproduce

Run `npm test` and `npm run check` in `portal/`. Start `PORT=8792 npm start` with the existing ignored server `.env`. With Playwright available externally (no new project dependency):

```sh
PLAYWRIGHT_MODULE=/absolute/path/to/playwright \
CHROME_PATH='/Applications/Google Chrome.app/Contents/MacOS/Google Chrome' \
PORTAL_URL=http://localhost:8792 \
E2E_ARTIFACTS=/tmp/il-context-e2e \
node agent/e2e-browser.cjs
```

Append `--live` for paid Dynamic verification. The runner has a 110-second ceiling and calls Leave in cleanup; inspect screenshots separately before making visual-identity claims. Artifacts remain local under the configured output directory. The original trial used `/tmp/il-context-e2e`; the revised failed trial uses `/tmp/il-context-e2e-v2`, and its successful fresh-session retry uses `/tmp/il-context-e2e-v2-retry`.

### Remaining acceptance

Keep issue #1 open until repeated clear live frames establish acceptable face preservation. The later combined Cartesia run again showed added people and character/clothing drift at rejoin, so visual continuity remains not passed despite successful transport and prompt tests. Prompt steering is not an identity-lock mechanism. Do not enable repeated automatic reseeding/reset to hide drift. Occlusion/side views, actual microphone input, live object pointing and S25/Air 2 Ultra behavior remain separate tests.

