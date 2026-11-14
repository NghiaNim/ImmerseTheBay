# Codex task 2 — 180°/360° immersive media + mic streaming hook

Branch: `feat/immersive-media`

## Context
`Assets/Scripts/Media/SpatialMediaPlayer.cs` plays a video/image on a quad with `Assets/Shaders/StereoUnlit.shader` (mono / side-by-side / top-bottom via `unity_StereoEyeIndex`). `Assets/Scripts/Audio/MicrophoneCapture.cs` records the mic into a looping clip and exposes `GetLatestSamples(float[])`. Content is spawned by `ContentSpawner` (not yours) from `StreamingAssets/content.json` items with `type` video/image and `layout` Mono/SideBySide/TopBottom.

## You own
- `Assets/Scripts/Media/**`, `Assets/Scripts/Audio/**`, `Assets/Shaders/**` (add files freely there)

## Do
1. **Projection modes**: add `MediaProjection { Flat, Dome180, Sphere360 }` to `SpatialMediaPlayer` (default Flat, existing behaviour unchanged). For Dome180/Sphere360 the player renders on an inward-facing procedural sphere/hemisphere mesh (generate it in code, radius default 10 m, centred on the user's head at start) instead of the quad, using an equirectangular mapping in the shader (add `_Projection` property to `StereoUnlit` or a new `ImmerseTheBay/StereoEquirect` shader). Stereo layouts must still work per eye. Keep the shader compatible with single-pass instanced / multiview (`UNITY_VERTEX_OUTPUT_STEREO` etc. as in the existing shader).
2. Add public `Play(string src, StereoLayout layout, MediaProjection projection)` overload; keep the old signature working (Flat).
3. **Playback controls API** on `SpatialMediaPlayer`: `Pause()`, `Resume()`, `TogglePause()`, `Seek(double seconds)`, `SetVolume(float 0..1)`, `IsPlaying`, `Duration`, `Time`. (UI for them is another task; just expose the API and show state in the existing `DebugRegistry` text.)
4. **Mic PCM stream**: in `MicrophoneCapture`, add `event Action<short[] pcm16, int sampleRate> OnPcmChunk` that fires on the main thread every `chunkMs` (default 40 ms) with new samples since the last chunk (handle the ring-buffer wrap; no duplicated or dropped samples), converted to 16-bit mono. Add `public int chunkMs`. Do not reference `NetClient` (networking is another task; they will subscribe to this event).
5. Document the new `projection` values for content.json in the PR (field name `projection`: Flat / Dome180 / Sphere360) — the content owner will add the field to `ContentItem`.

## Done when
- Compile check in `AGENTS.md` passes with no `error CS`.
- In Play mode in the editor with a local equirect test image/video (any file; do not commit large media), Sphere360 shows the panorama around the camera with correct orientation (not mirrored), Flat still works.
- Unit-style check: a small editor-only test or play-mode script proving `OnPcmChunk` delivers contiguous samples across the clip wrap (describe the method in the PR if no test framework is set up).
