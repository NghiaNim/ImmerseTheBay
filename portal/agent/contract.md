# Learning turn contract

`POST /api/agent/turn`, JSON, authenticated with the same `portal_token` header/query as `/api/token`.

```json
{"sessionId":"reactor-session-id","lessonId":"ja-home-001","beat":0,"transcript":"","imageRef":{"id":"opaque-upload-reference","caption":"A tatami room with a low table."},"context":{"gesture":null,"pointedAt":null,"roomLabels":["table","door"],"planes":{"horizontalAtDeskHeight":true},"gaze":"overlay","rms":0.12,"glassesKey":null}}
```

`beat` is the **zero-based flattened line cursor**, flattening `beats[].lines[]` in file order. Start at 0. It is not the index into `beats[]`. The response on `next_line` contains the **next** target overlay; increment the cursor once. On `stay` keep it. On `scene_done` stop requesting turns. The final response has `overlay:null`. A new session starts a new run. Requests on the same session are serialized. Clients must await each turn and command sequence before sending another turn.

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

## Restarting a run

`POST /api/agent/reset` with `{sessionId,lessonId}` returns `{ok:true}` and clears lesson initialization/hints, preserving the session-wide prompt rate limit. Await any pending turn first (otherwise 409). When starting fresh on a joined session, await Reactor `reset`, call this endpoint, then upload/set the image and request beat 0. This changes a shared scene for all joined clients.

## Sync

The driving client broadcasts `{type:"learn", sessionId, lessonId, beat, ...response}` **after** commands finish; `beat` is the new cursor after applying `advance`. Receiving clients render the event without executing `visko` again. `/sync` already relays arbitrary message types.

## Configuration and operations

`ANTHROPIC_API_KEY` stays in `portal/.env`. Optional `ANTHROPIC_MODEL` overrides `claude-opus-5-5` (current general-purpose latest model verified against the official model overview on 2026-11-14). `ANTHROPIC_API_VERSION` defaults to `2026-10-21`. The built-in fetch client uses `POST https://api.anthropic.com/v1/messages`, `x-api-key`, a 12-second timeout, and JSON schema output. No added dependencies.

Decision records are JSONL under `portal/agent/logs/`, ignored by the local `.gitignore`. They include transcript/context and should be retained locally only; no keys, raw photos or tokens are logged. Session state expires after 6 hours and is bounded to 500 sessions. Restarting the portal resets session initialization; reconnect with a fresh Reactor session.

Sources: [Anthropic models](https://platform.claude.com/docs/en/models/overview), [Messages API](https://platform.claude.com/docs/en/api/messages/create), [Reactor schema](https://docs.reactor.inc/model-api-reference/visko-orbis-stable/schema.md), [prompt guide](https://docs.reactor.inc/model-api-reference/visko-orbis-stable/prompt-guide.md). Native resolution follows this project's required glasses contract; the deployment must advertise/support it for the glasses path. The user-approved web-only exception is documented above.
