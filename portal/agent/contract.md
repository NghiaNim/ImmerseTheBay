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
- With a key, an unknown explicit goal can use a separate model planning call (OpenAI takes precedence; Claude remains available when only its key is configured). Strict plan shape: `{label,returnBeat,nodes:[{jp,kana,romaji,zh,en,cue}]}`. Every generated overlay is a learner/customer utterance fulfilling the exact primary intent; clerk actions belong only in scene cues, and the saved main target is solely a return anchor. The server rejects recognizable clerk-role phrases and validates 1–3 bounded nodes, unchanged return anchor, complete multilingual overlays, brand guardrails and continuous-scene cues before accepting it. Invalid/error plans leave the backbone untouched. Generated lines are scored and advanced just like authored branch lines.
- During an already-started detour, commands only steer the visible change via `set_prompt`; they do not `reset` or `start` the session. The same shop, assistant, lighting and camera identity persist. Main prompt refinement cannot replace branch overlays or control branch progression.

## Restarting a run

`POST /api/agent/reset` with `{sessionId,lessonId}` returns `{ok:true}` and clears lesson initialization/hints, preserving the session-wide prompt rate limit. Await any pending turn first (otherwise 409). When starting fresh on a joined session, await Reactor `reset`, call this endpoint, then upload/set the image and request beat 0. This changes a shared scene for all joined clients.

## Sync

The driving client broadcasts `{type:"learn", sessionId, lessonId, beat, ...response}` **after** commands finish; `beat` is the new cursor after applying `advance`. Receiving clients render the event without executing `visko` again. `/sync` already relays arbitrary message types.

## Configuration and operations

Keys stay only in the ignored `portal/.env` file. There is no browser key input, credential endpoint or browser storage. Set `OPENAI_API_KEY` and restart `npm start` to use OpenAI for both normal lesson turns and generated detour plans. Optional `OPENAI_MODEL` overrides `gpt-6-luna` (user-requested latest fast model; official support verified on 2026-11-14). For GPT-6 Luna, `OPENAI_REASONING_EFFORT` defaults to `none` for low latency; this parameter is omitted for other model overrides. Model access depends on the key/project; inaccessible models use scripted behavior and are never automatically replaced by an older model. Gesture-only/context-only navigation after opening skips LLM refinement entirely. The adapter uses `POST https://api.openai.com/v1/responses`, Bearer authorization, `text.format` with strict JSON schema, `store:false`, and a 12-second timeout. It handles incomplete responses, refusals, invalid JSON and HTTP errors with the existing scripted fallback. When an OpenAI key is present, failures do **not** silently send the request to Claude. API credentials and provider error bodies never enter decision logs.

Without `OPENAI_API_KEY`, an optional `ANTHROPIC_API_KEY` in `portal/.env` retains the previous Claude path; without either key the deterministic lesson and supported detours run locally. Optional `ANTHROPIC_MODEL` overrides `claude-opus-5-5` (current general-purpose latest model verified against the official model overview on 2026-11-14). `ANTHROPIC_API_VERSION` defaults to `2026-10-21`. The built-in fetch client uses `POST https://api.anthropic.com/v1/messages`, `x-api-key`, a 12-second timeout, and JSON schema output. No added dependencies.

Decision records include intent, route, forward graph trace and response. Decision records are JSONL under `portal/agent/logs/`, ignored by the local `.gitignore`. They include transcript/context and should be retained locally only; no keys, raw photos or tokens are logged. Session state expires after 6 hours and is bounded to 500 sessions. Restarting the portal resets session initialization; reconnect with a fresh Reactor session.

Sources: [OpenAI structured outputs](https://developers.openai.com/api/docs/guides/structured-outputs?api-mode=responses), [OpenAI Responses API](https://developers.openai.com/api/reference/resources/responses/methods/create), [GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna), [Anthropic models](https://platform.claude.com/docs/en/models/overview), [Messages API](https://platform.claude.com/docs/en/api/messages/create), [Reactor schema](https://docs.reactor.inc/model-api-reference/visko-orbis-stable/schema.md), [prompt guide](https://docs.reactor.inc/model-api-reference/visko-orbis-stable/prompt-guide.md). Native resolution follows this project's required glasses contract; the deployment must advertise/support it for the glasses path. The user-approved web-only exception is documented above.

### Recorded microphone transcription

`POST /api/agent/transcribe` uses the same portal authorization. Send the raw recorded audio blob with `Content-Type: audio/webm` (codec parameters allowed), `audio/mp4`, `audio/ogg`, `audio/wav`, `audio/x-wav`, or `audio/mpeg`. The optional `x-audio-language` header accepts `ja`, `zh`, or `en` and common regional forms (`ja-JP`, `zh-CN`, `en-US`); regional forms normalize to the two-letter hint. Maximum upload is 10 MiB; compressed HTTP request bodies are rejected. The response is `{ "text": "..." }`, including empty text for silence. The browser sends recognized text through the existing practice/transcript or intent flow; transcription alone never changes lesson state.

The server converts audio to multipart in memory and calls the fixed OpenAI `/v1/audio/transcriptions` endpoint with `OPENAI_API_KEY` and `OPENAI_TRANSCRIBE_MODEL` (default `gpt-transcribe`). No audio is written to disk or logged, and upstream errors never expose provider response bodies or credentials. Errors return safe `{error}` messages: 400 invalid/empty recording or language, 401 portal authorization, 413 oversized recording, 415 unsupported MIME type, 429 provider rate limit, 502 upstream failure, 503 missing server key, or 504 the 20-second provider timeout. There is no automatic model downgrade. A successful transcription can exceed the lesson endpoint's existing transcript/intent limits; clients should keep recordings short and respect those limits.

OpenAI's [file transcription guide](https://developers.openai.com/api/docs/guides/speech-to-text) and [gpt-transcribe model reference](https://developers.openai.com/api/docs/models/gpt-transcribe), checked 2026-11-14, recommend this model for completed recordings and specify `languages[]` multipart hints instead of the older singular `language` field. This bounded recording route does not use realtime streaming transcription.

## Run-scoped continuity and model context

Every outgoing visual `set_prompt` receives server-owned character/scene constraints after optional model refinement, including scripted fallback and detour/rejoin. The composer reserves the existing 600-character / 95-word / three-sentence budget for these constraints. Video audio prompts are finalized as environmental sounds only, without voices, dialogue, narration or music. This is steering, not a guarantee of facial identity preservation.

The first turn pins the image reference and caption to the lesson run. A later nonempty different `imageRef` returns 409; reset before replacing it. Omitting the reference on later turns retains the anchor. The existing reset API creates a fresh run on the next turn.

Both model adapters use the same operation-specific allowlist and 12,000-byte UTF-8 input ceiling. Whole beat lines/vocabulary and cumulative graphs are omitted; at most three distinct issued-action summaries are retained, explicitly not visually confirmed. Transcripts/sensor observations are turn-local. Completed detours retire their action memory, and the main checkpoint is updated before refinement after progression. Optional context is pruned before required fields; an oversized required payload fails locally and uses existing service fallback behavior. No public response-schema or Unity client change is required.

## Cartesia speech

The optional `speech` response field is null or `{role:"clerk",text,language:"ja"}`. It is separate from the learner overlay and emitted on opening and node transitions; hints do not repeat it automatically. Initial clerk lines are deterministic, original acknowledgments/directions keyed to cues; no inventory, price or payment facts are invented.

`POST /api/agent/speak` accepts `{text,language}` (1–1,000 characters; ja/en/zh), requires the usual portal authorization and returns no-store WAV bytes. Cartesia key, voice and model stay in server env. Admission is two concurrent requests per server instance, with a 15-second deadline and 8 MiB response limit. Browser playback cancels on next turn, mic start and Leave, and restores ducked video volume. Cartesia and Visko have independent clocks; no lip-sync or perfect video voice suppression is claimed.
