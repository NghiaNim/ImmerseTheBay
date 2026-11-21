# Three-step generated practice — issue #24

Branch: `codex/demo-three-steps`; base: `3e02168`.

## Ownership

Owned files: `portal/scenarios/generate.js`, `portal/practice/service.js`,
`portal/scenarios/scenarios.test.js`, `portal/practice/service.test.js`, and this brief.
The shared original checkout is read-only. Commit locally; do not push, deploy,
call paid providers, or edit draft, scoring, client, route, or Unity owners' files.

## Contract

Both generators use the explicit `demo-three-steps` profile: one beat containing
exactly three learner exchanges. Default level is N5; an explicit N5–N1 level
is preserved. Generated lessons carry `generationProfile` metadata; authored
lessons keep their existing structure and validation.

Each learner reply, partner utterance, and generated alternative is one short
sentence, at most 40 Japanese characters and 60 reading characters. These are
size/segmentation guardrails, not a proof of JLPT vocabulary or naturalness.
Prompts request one primary intent and 2–3 teachable words per exchange; repeated
words are welcome. The lesson vocabulary list contains 2–6 items. Invalid output
is regenerated once, then rejected; it is never clipped into three turns and no
unrelated canned fallback is substituted.

## Requests for owners

- Phone/client: replace the hardcoded N4 preparation default with N5 while
  preserving explicit learner selections. Show three base targets separately
  from temporary branch practice, and distinguish assisted completion.
- Scenario route: allow omitted `level` to reach the generator's N5 default;
  the existing route still requires an explicit level.
- Draft owner: retain `generationProfile` when publishing/storing generated
  lessons and carry optional photo `level` through strict draft input parsing.
- Scoring owner: calibrate natural alternatives, bounded assistance, and
  critical-substitution rejection independently; no score thresholds change here.

## Verification

Offline verification on 2026-11-15:

- `node --test portal/scenarios/scenarios.test.js portal/practice/service.test.js`:
  17/17 passed. Covers N5 defaults, explicit N4/N1, strict count/schema agreement,
  short Japanese examples, learner/partner/alternative budgets, two/four-turn
  rejection and bounded repair/failure, retained photo visibility and storage,
  and completion after the third target through the existing turn engine.
- `npm test --prefix portal`: 207/207 passed.
- `npm run check --prefix portal`, explicit `node --check` of both generator
  modules, and `git diff --check`: passed.
- Existing scenario response assertion now uses `WIRE_RESPONSE_SCHEMA` for the
  server-owned metadata; model output validation remains unchanged.
- Authored longer lessons still pass general lesson validation and bundled
  presets load without acquiring the generated profile.

Real-provider generation, S25 speech, wearer acceptance, and Unity compilation
were not run. No paid providers, deployment, or push. The hard size limits do not
establish semantic simplicity or fluent pronunciation; those require provider
and learner validation. Requests above remain with their owners.
