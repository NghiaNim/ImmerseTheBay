# Learning turn contract

`POST /api/agent/turn`, JSON, authenticated with the same `portal_token` header/query as `/api/token`.

```json
{"sessionId":"reactor-session-id","lessonId":"ja-home-001","beat":0,"transcript":"","imageRef":{"id":"opaque-upload-reference","caption":"A tatami room with a low table."},"context":{"gesture":null,"pointedAt":null,"roomLabels":["table","door"],"planes":{"horizontalAtDeskHeight":true},"gaze":"overlay","rms":0.12,"glassesKey":null}}
```

`beat` is the **zero-based flattened line cursor**, flattening `beats[].lines[]` in file order. Start at 0. It is not the index into `beats[]`. The response on `next_line` contains the **next** target overlay; increment the cursor once. On `stay` keep it, even when a detour changes the overlay. The server enforces the saved main cursor after initialization; a mismatched `beat` returns 409. On `scene_done` stop requesting turns. The final response has `overlay:null`. A new session starts a new run. Requests on the same session are serialized. Clients must await each turn and command sequence before sending another turn.

`imageRef` is optional: an opaque string or `{id,caption}`. The service treats a caption as untrusted descriptive context; it does not fetch arbitrary URLs or pretend a filename is visual recognition. The browser uploads the photo to Reactor and awaits `set_image` **before** the first turn. The agent does not upload or resend the image.

```json
{"visko":[{"name":"set_prompt","data":{"prompt":"The host places a cup on the table."}}],"overlay":{"jp":"…","kana":"…","romaji":"…","zh":"…","en":"…","hintLevel":0},"feedback":{"score":87,"notes":["Text similarity only; this does not measure pronunciation."]},"advance":"next_line"}
```

- The first turn returns `set_prompt`, `set_audio_prompt`, `set_resolution` with `{resolution:"native"}`, then `start` with `{}`. Native immediately precedes every start. Execute returned commands in order and await each.
- Browser-only deployment exception approved by the user: inspect the live `state.available_resolutions`; when `native` is absent and `1080p` is offered, the web client may execute the resolution command as `{resolution:"1080p"}` before `start`. The tested deployment offered `1080p`, `2k`, and `4k`. This fallback is restricted to the web demo; the service response and Android/glasses path retain `native`. Do not silently raise glasses resolution.
- `set_prompt` is emitted no more than once per 2000 ms per Reactor session (even across lesson IDs); the service waits for the next slot instead of discarding a prompt.
- Pinch / glasses `next` advances one line after initialization; long pinch / `repeat` raises hints without advancement. A point exposes matching vocab in feedback and steers toward the object. Room labels, gaze, desk plane and voice activity influence hints/prompts. The first turn presents the requested line without advancing.
- Nonempty transcript is scored against normalized kana and written Japanese; score >=75 advances, unless a hint/point gesture is present. This is edit-distance text similarity, not acoustic pronunciation scoring. Kanji readings are not inferred. Exact written target or kana target both score 100.
- LLM output is schema-checked, repaired/clamped, then reduced to safe visual prompt/audio/feedback suggestions. Authored lesson lines and deterministic progression stay authoritative. No key, timeout, malformed output or provider error uses deterministic fallback.
- `context.gesture`: `pinch|long_pinch|point|null`; `gaze`: `visko|overlay|room`; mesh labels are lowercase identifiers. Unknown/oversized input returns 400. Unknown lesson returns 404. Protected/private lessons are never served.

## Lesson discovery and images

- `GET /api/agent/lessons` → `{lessons:[{id,title,level,startImage,lineCount}]}`
- `GET /api/agent/lessons/:id` → the lesson JSON from `Assets/StreamingAssets/lessons/<id>.json`
- `GET /lessons/images/:filename` → a flat, locally stored `.jpg`, `.jpeg`, `.png` or `.webp` file. Other files and nested/private paths are denied. `startImage` uses `lessons/images/<filename>`; browser prepends `/`.
- Catalog and images use the same portal authorization. For an image element use `?portal_token=...`; fetch requests can use the header.

## Flexible lesson graph and spontaneous goals

The authored `beats[].lines[]` are the main lesson backbone. The server keeps a pending main checkpoint while optional detours run; the learner can finish the detour and resume that checkpoint rather than restart the scene.

Optional request `intent` is **1–500 characters of a natural-language goal**, in Chinese, Japanese or English, e.g. `"我想拿货架上的三明治"`. This is not a Visko prompt. A microphone can offer separate **Practice line** (send `transcript`) and **Say what I want** (send `intent`) modes. Explicit intent is not pronunciation-scored. Low-similarity practice transcripts trigger automatic intent only when they contain both clear desire/search wording and a concrete shopping target; a lone product word or a poor pronunciation attempt stays practice.

Responses retain `visko`, `overlay`, `feedback`, `advance` and add:

```json
{"route":{"kind":"detour","nodeId":"opaque-run-id:visit-2","label":"サンドイッチを選ぶ · Choose a sandwich","returnBeat":6,"step":1,"total":3}}
```

- `kind`: `main`, `detour`, or `rejoin`. `step` is **one-based** inside a detour; `step:0,total:0` outside. `returnBeat` is the zero-based saved main line cursor. `nodeId` identifies a fresh runtime visit, not an array index to send as `beat`.
- A new detour returns its first multilingual target overlay with `advance:"stay"`. Practice is scored against that active detour line, not the interrupted main line. A score >=75 or pinch advances to the next branch node, still with `advance:"stay"`.
- Finishing the last branch line returns the saved main overlay, `route.kind:"rejoin"`, and `advance:"stay"`. The following successful main-line practice resumes normal `next_line` progression. **Never increment client beat during branch or rejoin responses.**
- The saved checkpoint is exactly the interrupted line. A model cannot jump to checkout or rewrite that return anchor. Optional lesson `graph.checkpoints` labels describe the authored backbone; `detourReturnPolicy:"pending-checkpoint"` documents this invariant.
- Every runtime transition allocates a fresh visit ID and a forward edge. Returning to a logical main checkpoint creates a new visit; it does not create an edge back to an earlier node. The graph is acyclic, including repeated detours. A bounded 100-node/edge audit trace is logged with each decision; visit counters continue increasing after trace eviction.
- One active detour at a time, at most six detours per lesson run, 1–3 nodes each. New intent during an active detour stays on its current target with an explanatory note. Reset clears the run and allocates a fresh run ID. Existing request serialization and 2000-ms prompt spacing apply to branch transitions too.
- Without an LLM key, `ja-konbini-001` supports original sandwich, checkout-counter and generic product-shelf detours. The sandwich branch asks about contents/availability rather than asserting stock or dietary safety. Other goals remain at the main checkpoint with an availability note.
- With a key, an unknown explicit goal can use a separate model planning call (OpenAI takes precedence; Claude remains available when only its key is configured). Strict plan shape: `{label,returnBeat,nodes:[{jp,kana,romaji,zh,en,cue}]}`. The server validates 1–3 bounded nodes, unchanged return anchor, complete multilingual overlays, brand guardrails and continuous-scene cues before accepting it. Invalid/error plans leave the backbone untouched. Generated lines are scored and advanced just like authored branch lines.
- During an already-started detour, commands only steer the visible change via `set_prompt`; they do not `reset` or `start` the session. The same shop, assistant, lighting and camera identity persist. Main prompt refinement cannot replace branch overlays or control branch progression.

## Restarting a run

`POST /api/agent/reset` with `{sessionId,lessonId}` returns `{ok:true}` and clears lesson initialization/hints, preserving the session-wide prompt rate limit. Await any pending turn first (otherwise 409). When starting fresh on a joined session, await Reactor `reset`, call this endpoint, then upload/set the image and request beat 0. This changes a shared scene for all joined clients.

## Sync

The driving client broadcasts `{type:"learn", sessionId, lessonId, beat, ...response}` **after** commands finish; `beat` is the new cursor after applying `advance`. Receiving clients render the event without executing `visko` again. `/sync` already relays arbitrary message types.

## Configuration and operations

Keys stay only in the ignored `portal/.env` file. There is no browser key input, credential endpoint or browser storage. Set `OPENAI_API_KEY` and restart `npm start` to use OpenAI for both normal lesson turns and generated detour plans. Optional `OPENAI_MODEL` overrides `gpt-5.4-mini` (official supported lightweight model verified on 2026-11-14). The adapter uses `POST https://api.openai.com/v1/responses`, Bearer authorization, `text.format` with strict JSON schema, `store:false`, and a 12-second timeout. It handles incomplete responses, refusals, invalid JSON and HTTP errors with the existing scripted fallback. When an OpenAI key is present, failures do **not** silently send the request to Claude. API credentials and provider error bodies never enter decision logs.

Without `OPENAI_API_KEY`, an optional `ANTHROPIC_API_KEY` in `portal/.env` retains the previous Claude path; without either key the deterministic lesson and supported detours run locally. Optional `ANTHROPIC_MODEL` overrides `claude-opus-5-5` (current general-purpose latest model verified against the official model overview on 2026-11-14). `ANTHROPIC_API_VERSION` defaults to `2026-10-21`. The built-in fetch client uses `POST https://api.anthropic.com/v1/messages`, `x-api-key`, a 12-second timeout, and JSON schema output. No added dependencies.

Decision records include intent, route, forward graph trace and response. Decision records are JSONL under `portal/agent/logs/`, ignored by the local `.gitignore`. They include transcript/context and should be retained locally only; no keys, raw photos or tokens are logged. Session state expires after 6 hours and is bounded to 500 sessions. Restarting the portal resets session initialization; reconnect with a fresh Reactor session.

Sources: [OpenAI structured outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=responses), [OpenAI Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create), [GPT-5.4 mini](https://developers.openai.com/api/docs/models/gpt-5.4-mini), [Anthropic models](https://platform.claude.com/docs/en/models/overview), [Messages API](https://platform.claude.com/docs/en/api/messages/create), [Reactor schema](https://docs.reactor.inc/model-api-reference/visko-orbis-stable/schema.md), [prompt guide](https://docs.reactor.inc/model-api-reference/visko-orbis-stable/prompt-guide.md). Native resolution follows this project's required glasses contract; the deployment must advertise/support it for the glasses path. The user-approved web-only exception is documented above.
