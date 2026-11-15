# Photo-grounded micro-practice

`POST /api/practice/generate` accepts authenticated JSON `{image, goal}`. `image` is a JPEG/PNG/WebP base64 data URL, at most 3 MiB decoded; `goal` is a nonempty string of at most 500 characters. File signature and canonical base64 validation reject mislabeled inputs. Full image decoding is delegated to the vision provider; signature validation alone does not prove a complete image.

The service sends the actual image and goal as separate multimodal input parts to the existing configured OpenAI Responses model (default `gpt-6-luna`, reasoning `none`, no fallback). Strict structured output plus local bounds produce 2–4 learner lines with Japanese, kana, romaji, Chinese and English translations, cues, and 1–8 vocabulary items. Prompts distinguish visible partners from an off-camera partner, avoid inventing people or objects, and keep a fixed first-person scene. Model grounding is probabilistic and needs real-photo review; schema validation cannot verify all visual claims.

Response: `{lesson, expiresAt}`. Generated lessons include required boolean `visiblePartner`, grounded only in whether a conversation partner is actually visible in the photo. False means an off-camera partner; visual composition must not append mouth-motion instructions. The lesson uses the existing schema and loads through `/api/agent/lessons/:id`; it is not added to the public preset list. `lesson.startImage` is an opaque authenticated `/api/practice/images/:id` URL, never a base64 photo embedded into agent context. The frontend can retain its original selected File for immediate playback setup.

Local storage is memory-only, at most 32 generated lessons/photos, each expiring after 30 minutes. Expiry and oldest-first eviction remove photo and lesson together. No photo is written to disk or logged. OpenAI receives the photo with `store:false`; that flag is not a claim about all provider-side retention policies. Server restart deletes generated lessons. After expiry, clients must generate a new lesson; retries do not silently recover old photos.

Two generations may run concurrently. Each caller has a five-second cooldown; the bounded admission table holds at most 128 recent callers. Provider calls time out after 30 seconds and abort when the HTTP client disconnects. Error bodies never echo upstream responses, image data or credentials.

The route must be registered before the global 64 KiB JSON parser. `server.js` passes one lesson store to practice generation, agent routes, and agent service, preserving normal runtime/branching behavior for generated IDs.

Run mocked contract and HTTP tests with:

```sh
node --test portal/practice/service.test.js
```

These tests verify shape, multimodal request construction, photo-free lesson context, ephemeral expiry/eviction, authentication and caching. They do not demonstrate model quality or real voice/video generation; report those E2E checks separately.
