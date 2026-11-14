# Authoring lessons

Lessons are original conversation material in `Assets/StreamingAssets/lessons/<id>.json`. The default lesson, `ja-konbini-001`, is shopping at an invented Japanese neighborhood convenience store. Its five beats contain ten learner utterances: finding food, asking about products, comparing prices, checkout and packing, and a polite farewell. N1 is the intended learner level; everyday shopping deliberately mixes advanced phrasing with common natural expressions rather than forcing exam grammar into every sentence. There are no real chain or product brands in the prompts.

`ja-home-001` remains an optional home-visit lesson, not the default shopping scenario. Its images must not be used as convenience-store reference photos. The store lesson uses `lessons/images/konbini-checkout-original.png`, an original AI-generated, brand-free checkout illustration approved by the user as an explicit exception to the CC0/public-domain photo policy. It is not represented as a photograph or a CC0 asset. Its provenance is recorded in `images/SOURCES.md`.

## File schema

All fields below are required. Strings contain plain text, not HTML or executable instructions. `startImage` may be empty when there is no approved bundled reference; other text fields must be nonempty. Keep lesson IDs to lowercase ASCII letters, digits and hyphens; the filename must match `id`.

```json
{
  "id": "ja-home-001",
  "title": "A human-readable title",
  "level": "N1",
  "scenePrompt": "One to three sentences establishing the initial visible scene.",
  "audioPrompt": "One sentence describing sounds only.",
  "startImage": "lessons/images/tokyo-house-interior.jpg",
  "beats": [
    {
      "goal": "What the learner practices in this beat.",
      "lines": [
        {
          "jp": "お茶にしましょうか。",
          "kana": "おちゃにしましょうか。",
          "romaji": "Ocha ni shimashou ka.",
          "zh": "我们喝点茶吧。",
          "en": "Shall we have some tea?",
          "cue": "The host sets a teacup on the table."
        }
      ],
      "vocab": [
        { "jp": "机", "kana": "つくえ", "en": "table", "objectHint": "table" }
      ]
    }
  ]
}
```

When nonempty, `startImage` is relative to Unity's `StreamingAssets` directory. The web server exposes approved public images beneath `/lessons/images/`; it must not expose the whole StreamingAssets tree. Arrays preserve the teaching order. Each beat has at least one line and a vocabulary array. A line is a learner utterance, not a host response. `cue` is the small visible action the host or environment can perform as that line is rehearsed. Follow the authoritative request/response contract and progression rules in `portal/agent/contract.md`; a beat can contain several lines, so clients must not assume one turn equals one beat.

`kana` spells out the complete reading, including kanji readings, with Japanese punctuation retained. Particles keep normal orthography (`は`, `へ`, `を`); romaji uses their spoken values (`wa`, `e`, `o`). Romaji uses ASCII Hepburn-style spelling and explicit long vowels (`ou`, `uu`) to simplify display. Translations preserve meaning and conversational tone rather than word order. The sample's final `かたがた` farewell is intentionally somewhat formal; the goal says so. A fluent speaker should review future material before a learning release.

## Spatial vocabulary

Use the lowercase classification labels `table`, `door`, `wall`, `floor`, and `ceiling` in `objectHint`. This is the surface or place associated with a word, not a claim that the mesh recognizer can identify the object itself. For example, `湯飲み` can be suggested when pointing at a `table`; do not tell the learner the table *is* a cup. Keep at least one literal surface word available for each relevant label. Missing or uncertain labels must remain unknown instead of inventing a detection.

For the store lesson, `table` grounds the checkout counter and items being discussed there, `door` grounds the entrance/exit, `wall` grounds wall-side shelves, and `floor` grounds the aisle. These mappings are teaching associations, not additional object-recognition capabilities.

## Prompt and image rules

- Write original scenes and dialogue. Do not copy textbook passages, named fictional characters, franchise designs, brand imagery, or prompts requesting imitation of those properties. Broad settings and generic anime-inspired rendering are acceptable.
- Establish subject, action, place and camera once in `scenePrompt`, using 1–3 sentences and fewer than about 100 words. Later cues describe one visible change; keep the existing room and cast continuous. Avoid cuts and compound action sequences.
- Keep `audioPrompt` about audible materials and ambience. Do not reuse the visual description as an audio prompt.
- Send `set_resolution` with `native` before `start`. Runtime code must enforce at most one `set_prompt` in roughly two seconds and allow time for the visible transition.
- Start images must have documented CC0/public-domain rights, or stay private, unless the user explicitly approves an exception. The original generated convenience-store illustration is such an exception; this does not authorize arbitrary generated replacements. Record the exact source page, creator, download URL, license and any modifications in [SOURCES.md](../Assets/StreamingAssets/lessons/images/SOURCES.md). A metadata CC0 license is not automatically a license for the image.
- Prefer 16:9 landscape photos. The default photo already has that ratio. Crop a portrait reference deliberately before upload; Reactor otherwise squashes it. The historic photo and garden are optional reference experiments, not period-accurate depictions of the invented 1970s home. The garden may pull generation outdoors, so use the default interior for the canonical lesson test.

These rules follow the [official Reactor stable prompt guide](https://docs.reactor.inc/model-api-reference/visko-orbis-stable/prompt-guide.md), checked on 2026-11-14. Image licensing is recorded per file rather than inferred from a search result.

## Private local excerpts

If a learner has material they are entitled to use locally, keep it under `Assets/StreamingAssets/lessons/private/` and never commit it or its Unity metadata. Do not paste it into public lesson files, issue descriptions, test fixtures, decision logs, or sample assets. Gitignore alone does not protect a public HTTP route, and StreamingAssets is copied into Android builds: exclude the private folder from redistribution builds. Public lesson enumeration and image serving must explicitly exclude this directory. Sending private material to an LLM is a separate user choice; the demo's public catalog does not load it automatically.

Integration request: add `/Assets/StreamingAssets/lessons/private/` and `/Assets/StreamingAssets/lessons/private.meta` to the root `.gitignore`; preserve that exclusion when building or packaging distributable artifacts.

## Checks before sharing

Parse the JSON, check that each required field is present and nonempty, validate every `objectHint`, and verify the local start image exists. Read kana and translations against each Japanese line. Check source-page licenses before adding binaries. The content stream does not launch Unity; the coordinator must import assets, commit their generated `.meta` files, and run the integrated portal and Unity checks.
