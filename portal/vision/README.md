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

For small products, the browser sends a region around the selected point: 0.24 of the visible image width by 0.36 of its height, clamped to the image edges, then resized to 640 pixels wide at JPEG quality 0.9. Normalize the point against this region, not the original full frame. If the region in full-frame normalized coordinates is `(rx, ry, rw, rh)`, send `((px-rx)/rw, (py-ry)/rh)` and map returned boxes back with `x=rx+box.x*rw`, `y=ry+box.y*rh`, `width=box.width*rw`, `height=box.height*rh`. Clamping means the point is not always the region's center. The server validates geometry in the submitted crop's coordinate system; a crop does not provide depth or temporal tracking.

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

Live static-photo check on 2026-11-14: a small refrigerator bottle at full-frame point `(0.359, 0.446)` returned a raw null detection from a 960-pixel-wide full image. The region crop above returned `bottle`, confidence `0.95`, in approximately 1.6 seconds, with the original confidence and geometry gates unchanged. This verifies that one example and the browser-to-service path; it is not a general accuracy measurement or a live moving-video test. Uncertain or unsupported targets must still return no hit.

Official documentation checked 2026-11-14:

- [GPT-6 Luna](https://developers.openai.com/api/docs/models/gpt-6-luna): image input, structured outputs and `none` reasoning are supported.
- [Responses create](https://developers.openai.com/api/reference/resources/responses/methods/create): image data URLs and strict `text.format` JSON Schema.
- [Images and vision](https://developers.openai.com/api/docs/guides/images-vision): image input and spatial-reasoning limitations.

Integration note (2026-11-14): browser start-image pointing → real Luna bottle detection → learner-goal DAG succeeded in 3.556 s; person and blank-wall controls produced no branch. A live Dynamic frame check was blocked before connection by Reactor HTTP 429 `no available capacity`; no Stable substitution was made.

Accuracy limit: a repeated live-model request at the same tiny bottle also returned no-hit, including a hold/select trial. The prototype therefore has observed false negatives; a successful single-frame result is not a reliability benchmark. No-hit leaves the lesson unchanged. Physical hand tracking and 3D proximity remain outside this Web pointer prototype.
