# Pointing at objects inside video

This bounded prototype interprets a **2D point in one video frame**. It does not estimate physical distance, contact, hand pose, depth or object motion. XREAL mesh classification describes the real room; this service describes pixels inside the generated video. Keep these coordinate systems separate.

## Integration

Register before the portal's global 64 KiB JSON parser:

```js
import { registerVisionRoutes } from './vision/routes.js';
registerVisionRoutes(app, { authorized });
app.use(express.json({ limit: '64kb' }));
```

`POST /api/vision/point`, using the existing `portal_token` authorization policy:

```json
{
  "frame": "data:image/jpeg;base64,...",
  "point": { "x": 0.63, "y": 0.54 },
  "gesture": "point"
}
```

Both coordinates range from zero to one, with the origin at the image's top-left. Capture the current visible video crop and compute the point in that exact crop. Account for CSS letterboxing/cropping, mirroring and any field-of-view mask before sending; do not normalize against a containing element with different geometry. Resize to about 640 pixels wide before encoding JPEG. A photograph preview may be used for a clearly labeled static-image test, but must not be described as live video recognition.

The route accepts at most 3 MiB of JSON. The decoded JPEG is limited to 2 MiB, 4096 pixels per dimension and 8 million pixels overall. It accepts data URLs only, so it cannot fetch arbitrary image URLs. Header validation is bounded; the provider validates compressed-image decoding. Other JSON fields, remote URLs, non-JPEG inputs, invalid normalized numbers and unsupported gestures receive HTTP 400; size overflow receives 413.

The service reads `OPENAI_API_KEY` on the server and uses `gpt-6-luna` through Responses with `reasoning.effort: "none"`, image input, strict JSON Schema, a 12-second timeout and `store: false`. Missing configuration returns 503; provider failure returns a sanitized 502/503/504. Frames are neither written to disk nor included in application logs. They are sent to the configured OpenAI API account; `store: false` is not a claim about zero-retention eligibility.

## Result and scene steering

A grounded hit looks like:

```json
{
  "object": "bottle",
  "label": "ボトル",
  "confidence": 0.92,
  "bounds": { "x": 0.57, "y": 0.32, "width": 0.11, "height": 0.35 },
  "intent": "inspect"
}
```

No hit is always `{ "object": null, "label": null, "confidence": 0, "bounds": null, "intent": "none" }`. The server requires confidence ≥0.8 and a point inside the box or within 0.025 normalized units of its nearest edge. It rejects invalid, implausibly tiny or scene-sized boxes. Confidence is the model's uncalibrated estimate, not a measured probability; this geometry check cannot independently prove the model's box is correct. Motion, occlusion and stale frames remain practical limitations.

Only ordinary categories in `schema.js` are accepted. Japanese display labels come from the fixed server catalog, not OCR or generated text. Point maps to `inspect`; pinch maps to `select`, which means a selection gesture, not a confirmed purchase. Text in the image is explicitly treated as untrusted data and never interpolated into scene prompts. Never forward an unvalidated model reply directly to Reactor.

The caller can pass a successful canonical object/intent to the lesson agent as a learner goal, keeping the lesson's detour/rejoin behavior. Alternatively, `createVisionPrompt(result, originalPoint)` rechecks the hit and produces a short, brand-free visual delta, or returns `null`. The helper never emits commands itself. The caller retains the existing Visko prompt rate limit, awaits the current result, ignores stale results after session changes, and must not steer on a no-hit/error. A single-frame match means screen-space overlap only; demonstrating a hand “approaching” an object requires temporal tracking and a separate interaction rule.

Requests have a two-second per-caller cooldown, at most two simultaneous provider calls and a bounded timestamp-only throttle map. This is a demonstration, not a per-frame video detector; trigger it on deliberate point/pinch events or a stable dwell.

## Verification and references

Run `node --test vision/*.test.js` from `portal/`. Tests mock the provider and cover coordinate/image limits, low-confidence and off-point rejection, prompt injection boundaries, Responses payload, provider errors, request throttling, HTTP authorization and route-specific body limits. Unit fixtures test JPEG header handling, not full decoding. Live accuracy and actual account model access must be tested separately on representative frames.

Official documentation checked 2026-11-14:

- [GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna): image input, structured outputs and `none` reasoning are supported.
- [Responses create](https://developers.openai.com/api/reference/resources/responses/methods/create): image data URLs and strict `text.format` JSON Schema.
- [Images and vision](https://developers.openai.com/api/docs/guides/images-vision): image input and spatial-reasoning limitations.
