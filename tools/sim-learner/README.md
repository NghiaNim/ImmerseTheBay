# sim-learner

Scripted learner personas that drive the running Portal's lesson agent with **text turns only** and record what it does. Nothing here connects to Visko/Reactor or Cartesia: the HTTP helper refuses `/api/token`, `/api/voice/*` and `/api/agent/speak`, and returned `visko` commands are recorded, never executed.

```sh
cd portal && PORT=8793 node server.js        # in another terminal
node tools/sim-learner/sim.mjs               # all personas × ja-konbini-001, ja-home-001, one photo practice
node tools/sim-learner/sim.mjs --personas strong,detours --lessons ja-konbini-001 --keep-records
node tools/sim-learner/analyze.mjs tools/sim-learner/runs/<timestamp>   # re-run checks on a saved run
```

Options: `--base <url>` (or `PORTAL_URL`), `PORTAL_TOKEN` env, `--concurrency 4`, `--no-photo`, `--keep-records` (by default the saved-session records the runs create are deleted at the end, by their own run IDs only).

Cost per full run: about 250 turns, roughly 150 OpenAI lesson/planner calls, and one `/api/practice/generate` call with `Assets/StreamingAssets/lessons/images/tokyo-house-interior.jpg`. Session IDs start with `practice-sim-`, so the Portal treats them as practice mode and they are easy to find in `portal/agent/logs/`.

## Personas (`personas.mjs`)

| persona | what it does |
|---|---|
| `beginner` | Answers with the Chinese/English translation, asks for help in L1, reads romaji, desire-without-item vs desire-with-item (auto-intent) |
| `nearMiss` | Particle/conjugation errors on every line (は→が, を→が, ます→る, potential→plain…), then self-corrects |
| `adversarial` | Copyrighted characters (denylisted and not), caption/transcript/intent prompt injection with a canary, swearing, HTML, textbook-dialogue request |
| `silence` | Empty/whitespace/punctuation transcripts, idle hints, 990-char ramble, Japanese ramble containing the target, over-limit transcript/intent |
| `strong` | Exact kana, kanji form, natural paraphrases, a mid-lesson detour, skip-ahead attempt (beat+2), a turn after `scene_done` |
| `detours` | Detour → nested intent → finish → rejoin, planner detour + `return_to_main`, pinch through a detour, exhausts the six-detour cap |

## Output (`runs/<timestamp>/`)

- `<lesson>__<persona>.jsonl`: one line per turn with `request`, `status`, `latencyMs`, `response`, `before`/`after` (client beat cursor and DAG `route` node: kind, nodeId, step/total, returnBeat), the step's annotations (`meta`), and `decision` (source and failure from the Portal's own decision log, joined by session ID).
- `lessons/`: the lesson JSON each run used (including the generated photo practice).
- `meta.json`, `findings.json`, `summary.md`: run metadata, machine-readable findings, and the human summary.

Checks (`analyze.mjs`): wrong advance/reject against the persona's `shouldPass`, rejoin overlay versus the saved main line, return anchor, Visko command order and `native` before `start`, prompt length/word/sentence budget, scene and ambient anchors, camera moves, voices in audio prompts, learner text copied into prompts, `set_prompt` pacing, a wider character/profanity/injection-canary scan of everything the learner or Visko would receive, and LLM fallbacks.
