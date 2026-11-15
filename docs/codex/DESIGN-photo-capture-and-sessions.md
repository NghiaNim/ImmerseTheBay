# Design — photo capture and saved practice sessions

Status: **phase 1 + replay implemented on `feat/photo-sessions`** (2026-11-14). Scope: web app (`portal/`) first; every server piece is shared with the Unity/glasses client.

**Main demo flow (also implemented):** photo → describe the situation by voice or text → choose a JLPT level → the server writes an original conversation (partner lines, proposed answers with alternatives) → review → practice or live video. Built-in lessons stay under a Lessons tab as demos. Contract: "Generated scenarios" in `portal/agent/contract.md`. Per the user's direction, photos are **not** sent to OpenAI; only the request text is.

Implemented decisions (defaults, easy to change): plain files under `portal/data/` instead of a database; a single local learner (`userId: "local"`); no audio/video recording or photo captioning yet. Wire contract: "Photos, saved sessions and replays" in `portal/agent/contract.md`. Photo upload is JSON data URLs (`{image, thumb}`) rather than raw bytes, and thumbnails are made in the browser.

## 1. Goals

1. **Capture a photo** of what's in front of the learner and use it as the start image of a lesson scene.
2. **Save each lesson run** (photo, lines, what the learner said, scores, side detours, clerk conversation, optionally audio and video), so the learner can **review it and practice the same scenario again later**, with or without paying for live video.

Non-goals for this phase: spaced-repetition scheduling, social sharing, acoustic pronunciation scoring, Unity UI.

## 2. Design principles

| Principle | Why |
|---|---|
| **Record on the server, not in the page.** `service.turn()` already sees every decision; it writes the session record. | Glasses get recording for free; no second implementation in C#. |
| **Photos are server assets with a stable `photoId`.** Upload once, reuse for Reactor, review, replay and Unity. | Today the photo goes browser → Reactor only; its `uploadId` is session-scoped and the image is lost after Leave. |
| **Replay = a recorded session compiled into an ordinary lesson.** | The existing turn engine, scoring, hints, detours, TTS and Unity client run it unchanged. No new state machine. |
| **No changes to the turn contract.** The record ID is the existing `runId` (already returned in `response.operation`). | `LearningAgentClient.cs` and `agent/contract.md` stay valid. |
| **Storage behind an interface.** Local disk now, cloud later. | Laptop demo today, hosted service tomorrow, same API. |
| **Photos and voice are personal data.** Strip location metadata, record audio/video only with opt-in, support deletion. | Home interiors, faces and voice recordings. |

## 3. What exists today (verified in code)

- `learn.html` already has a file picker (`#photo`) and sample images. `preparePhoto()` center-crops to 1280×720 JPEG on a canvas (this also drops EXIF). The browser uploads to Reactor (`uploadFile` → `set_image`) and sends `imageRef = {id: reactorUploadId, caption}` to the agent. Caption for user photos is a placeholder: "visual contents have not been captioned".
- Lesson state lives in memory in `agent/service.js` (`sessions` Map, 6 h expiry). A portal restart loses it.
- Every decision is appended to `portal/agent/logs/YYYY-MM-DD.jsonl` (transcript, context, route, response). Not queryable per learner, no audio, no photo.
- Detour plans (including LLM-generated lines) exist only in `state.flow`; they are lost when the run expires.
- Transcription (`/api/agent/transcribe`) explicitly stores no audio.
- The Cartesia conversation runs browser ↔ Cartesia; the server only sees the tool calls (as turns) and the `call_id` it minted in `/api/voice/token`.
- Unity has `PhotoCapture.CaptureJpeg()` (S25 rear camera, 1280×720 JPEG) and uses the same `/api/agent/*` endpoints.

## 4. Photo capture

### 4.1 Web flow

```
[Take photo]  →  live viewfinder (rear camera, 16:9 guide)  →  capture → preview → [Use] / [Retake]
     │                                                                       │
     └─ fallback: <input type=file accept="image/*" capture="environment">  ─┘
                                                                              ▼
                                 POST /api/photos (JPEG) → {photoId, caption, warnings}
                                                                              ▼
                       lesson start: uploadFile(photo) → set_image → imageRef {id:"photo:<id>", caption}
```

- **Viewfinder:** `getUserMedia({video:{facingMode:'environment', width:1920, height:1080}})`, draw a 16:9 frame guide so what you see is what the scene gets (Reactor squashes to 832×480; we keep the existing center crop).
- **Fallback:** the `capture="environment"` file input opens the phone's native camera and works without HTTPS. `getUserMedia` (and the microphone) require HTTPS or localhost — see §8.
- **Photo library:** "My photos" grid (thumbnails) in Lesson setup, next to the bundled samples. Pick, reuse or delete.
- **Caption (optional, recommended):** on upload, one vision call (existing OpenAI key) returns a short neutral caption plus flags (`people`, `text/brand visible`). The caption replaces today's placeholder in `imageRef.caption`, giving the agent real scene context. Caption is run through `hasBannedTerm`; flags surface as gentle warnings ("a person is visible — the scene may animate them").
- **Suggest a lesson:** later, the caption can rank lessons ("kitchen" → home lesson). Out of scope for phase 1.

### 4.2 Server: `POST /api/photos`

- Body: raw `image/jpeg` (client already re-encodes), ≤ 4 MiB, ≤ 4096 px per side. Registered before the global 64 KiB JSON parser (same pattern as vision routes).
- Validate JPEG magic bytes and SOF dimensions; **strip any APP1 (EXIF/XMP) segments** by walking JPEG markers (no new dependency) so GPS never persists even if a client skips the canvas step.
- Store `photos/<photoId>.jpg` + a 320 px thumbnail (made client-side and uploaded alongside, to avoid an image library on the server).
- Response: `{photoId, width, height, caption, flags, createdAt}`.

### 4.3 Agent binding

`imageRef.id` becomes `"photo:<photoId>"` instead of the Reactor upload ID. The service already pins `referenceId` per run and rejects a changed reference with 409, so nothing else changes. The server can now resolve the photo for the session record.

### 4.4 Unity (later, no new server work)

`PhotoCapture.CaptureJpeg()` → `POST /api/photos` → keep `photoId` → existing Reactor upload → `imageRef "photo:<id>"`. Glasses photos appear in the web library automatically.

## 5. Session recording

### 5.1 Data model

```
User ─┬─< Photo
      └─< SessionRecord ─┬─< Event (append-only, ordered by seq)
                         └─< Media (audio attempt, video part)
```

**SessionRecord** — id = `runId`

| Field | Notes |
|---|---|
| `id` | the flow `runId` (already unique, already returned to clients) |
| `userId` | see §7 |
| `lessonId`, `lessonTitle` | |
| `photoId` | null for sample images (store the sample filename instead) |
| `mode` | `live` · `practice` · `replay-practice` · `replay-live` |
| `model` | `dynamic` / `stable` / null |
| `sourceRecordId` | set when this run is a replay of another record |
| `startedAt`, `endedAt`, `status` | `active` · `complete` · `left` · `expired` |
| `summary` | lines attempted / passed, average score, detours taken (denormalised for the list view) |

**Event** — written by the server unless marked *client*

| type | Content |
|---|---|
| `turn` | request (transcript, intent, action, gesture, `audioId`), response (overlay, feedback score/notes, route, advance, clerk speech, visual prompt) |
| `detour_plan` | the **full accepted plan** (label, returnBeat, nodes with jp/kana/romaji/zh/en/cue). Required for replay; today it's lost. |
| `conversation` *(client)* | `{role: user|clerk, text, at}` from the Cartesia live conversation |
| `lifecycle` *(client)* | start, leave, Reactor session id, resolution |

**Media**

| kind | Source | Default |
|---|---|---|
| `audio_attempt` | the recording already sent to `/api/agent/transcribe` | opt-in per learner |
| `video` | browser `MediaRecorder` on the Reactor `MediaStream`, uploaded in ~10 s parts | opt-in per session, off by default |

### 5.2 How each piece is captured

- **Turns and detours:** inside `service.turn()`, next to the existing `log(...)` call, call `recorder.append(runId, event)`. The first turn of a run creates the record. `reset` / expiry close it. No client change.
- **Audio attempts:** `/api/agent/transcribe` accepts header `x-record-audio: 1`. When set (and the user opted in), the server keeps the bytes it already holds and returns `{text, audioId}`. The client passes `audioId` in the turn request as a new optional field (additive; contract doc updated; Unity simply never sends it). One upload, no duplication.
- **Conversation transcript:** preferred: server fetches the call transcript from Cartesia by `call_id` after the call ends (server already has the ID; works for Unity too). **Needs verification** that the Cartesia Agents API exposes call transcripts. Fallback: the browser already receives `onText(text, role)` and posts batches to `POST /api/sessions/:id/events`.
- **Video:** `new MediaRecorder(mediaStream, {mimeType:'video/webm;codecs=vp9,opus', videoBitsPerSecond: 1_000_000})`, `timeslice` 10 s, each part `PUT /api/sessions/:id/video/:part`. ~7.5 MB per minute at native 640×368. Stops on Leave. Browser-only for now. **Check Reactor's terms on storing generated output** before enabling by default.

### 5.3 Storage

`SessionStore` interface: `createRecord`, `appendEvent`, `putMedia`, `getRecord`, `listRecords(userId)`, `deleteRecord`, `putPhoto`, `listPhotos`, `deletePhoto`.

- **v1 local:** `node:sqlite` (built into Node ≥ 22.13; no npm dependency; currently prints an experimental warning) at `portal/data/immerse.db`, media files under `portal/data/media/`. `portal/data/` gitignored; files written with mode 0600 like the decision logs.
- **v2 hosted:** Postgres (records/events) + object storage (S3, R2 or GCS) with signed URLs for media. Same interface.

## 6. Review and practice again

### 6.1 "My sessions" (`/sessions.html`)

List: photo thumbnail, lesson, date, lines passed / total, detours. Detail view is a timeline:

- photo (and video clip if recorded),
- each line: jp / kana / romaji / translations, your transcript, score, **your audio ▶ vs model voice ▶**,
- detours shown inline as an indented branch with its label,
- clerk conversation transcript.

Free to view. Model-voice playback calls `/api/agent/speak`; add a server-side cache keyed by `sha256(text+voice+model)` so repeated listening doesn't re-bill Cartesia.

### 6.2 Practice again

`POST /api/sessions/:id/replay {deck: "all" | "missed"}` compiles the record into a lesson object that passes `validateLesson`:

- id `rec-<shortid>`, title "Replay · <lesson> · <date>", `startImage` = the session photo,
- beats in the order the learner experienced them: main beats as authored; each detour becomes its own beat (goal = detour label, lines = the saved plan nodes, which already carry `cue`),
- `deck: "missed"` keeps only lines whose best score was < 75.

`createLessonStore` gets a second source: recorded lessons resolved by `rec-` ID (scoped to the owner) in addition to `StreamingAssets/lessons/`. The client then starts a lesson exactly as today, choosing:

| Mode | Cost | What happens |
|---|---|---|
| **Practice (no video)** | OpenAI only (transcribe + optional refinement) | existing practice mode, the photo as backdrop |
| **Live** | Reactor + OpenAI (+ Cartesia) | new Visko session, same photo via `set_image`; video is regenerated, not replayed (generative, not identical) |

New detours remain possible during a replay; they are recorded on the new run, linked by `sourceRecordId`.

## 7. Identity and access control

Today there is one optional shared `PORTAL_TOKEN`: everyone is the same user. Sessions per learner need an identity. Options:

| Option | Effort | When |
|---|---|---|
| A. **Single learner per portal** (no users) | none | solo dev/demo on one laptop |
| B. **Named profiles** behind `PORTAL_TOKEN` ("Who's practicing?") | small | trusted team demo; not secure between users |
| C. **Real sign-in** (Google sign-in or a hosted auth provider) | medium | before anyone outside the team uses it or it's hosted |

Recommendation: build the store with `userId` everywhere, ship **B** now, move to **C** when hosting. Unity later pairs with a profile via a short code shown on the web.

## 8. Using it from the Samsung phone (HTTPS)

Camera viewfinder and microphone need a secure context. `http://<mac-ip>:8791` on the S25 is **not** secure, so only the file-input camera fallback would work and the mic would not. Options:

| Option | Notes |
|---|---|
| **Tailscale** (`tailscale serve`) | Private HTTPS on your own devices; recommended |
| Cloudflare Tunnel / ngrok | Public HTTPS URL; **must** set `PORTAL_TOKEN` (otherwise anyone can burn API credits) |
| mkcert local CA | Install a CA certificate on the S25; fiddly on Android |

## 9. Privacy and safety

- Strip EXIF/GPS on upload (client canvas + server marker strip).
- Audio and video recording: explicit opt-in toggles, visible "recording" indicator, off by default.
- Retention default 90 days for media (configurable); records kept until deleted. `DELETE` for photo, session and "delete all my data".
- Photos are only sent to Reactor (as today) and, if captioning is enabled, OpenAI. Never written to decision logs.
- Captions and generated lines keep passing through existing `hasBannedTerm` / `safePrompt` guardrails.

## 10. API summary (new)

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/photos` | upload JPEG (+ thumbnail) → `{photoId, caption, flags}` |
| GET | `/api/photos` · `/api/photos/:id` · `/api/photos/:id/thumb` | library, image, thumbnail |
| DELETE | `/api/photos/:id` | delete photo (sessions keep a "photo deleted" placeholder) |
| GET | `/api/sessions` · `/api/sessions/:id` | list / full timeline |
| POST | `/api/sessions/:id/events` | client events (conversation text, lifecycle) |
| PUT | `/api/sessions/:id/video/:part` | video parts |
| GET | `/api/sessions/:id/media/:mediaId` | audio/video playback (owner only) |
| POST | `/api/sessions/:id/replay` | compile → `{lessonId:"rec-…"}` |
| DELETE | `/api/sessions/:id` | delete record + media |
| changed | `/api/agent/transcribe` | optional `x-record-audio` → `{text, audioId}` |
| changed | `/api/agent/turn` | optional `audioId`; server-side recording; recorded lessons resolvable |

All use the existing `authorized` check plus user scoping.

## 11. Phased plan

| Phase | Deliverable | Tests |
|---|---|---|
| **1** | `SessionStore` (sqlite + files), `POST/GET /api/photos` with EXIF strip, camera viewfinder + fallback + photo library in `learn.html`, `imageRef "photo:<id>"`, server-side turn + detour recording, `/sessions.html` list + timeline (text only) | store unit tests, JPEG strip tests, route tests, a turn → record integration test, existing 145 tests still pass |
| **2** | Replay compiler + `rec-` lessons, practice-again (all / missed), audio attempt storage + playback, TTS cache, photo captioning | compiler output passes `validateLesson`; replay run through the existing flow tests |
| **3** | Conversation transcript capture, opt-in video recording, retention + delete-all | chunked upload tests; retention job test |
| **4** | Profiles → real auth, hosted storage, Unity photo upload + pairing | — |

Phase 1 is roughly a focused branch (`feat/photo-sessions`) touching `portal/` and `portal/public/` only; no Unity, `ProjectSettings/` or package changes.

## 12. Open questions

1. Identity: A, B or C (§7)?
2. Is `node:sqlite` acceptable (requires raising `engines.node` to ≥ 22.13), or prefer plain JSON files, or approve one dependency?
3. Record audio attempts and video by default, opt-in, or not at all in phase 1?
4. Is photo captioning via OpenAI acceptable (extra call per photo, photo leaves the device to OpenAI as well as Reactor)?
5. Retention period for media.
6. Reactor terms on storing generated video; Cartesia transcript retrieval by `call_id` (to verify).
