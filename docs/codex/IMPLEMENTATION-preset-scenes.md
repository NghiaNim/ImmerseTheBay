# Preset branch scenes and video transitions

The demo should not rely on continuous model consistency across major location changes. Use three original reference scenes: checkout, sandwich display, and beverage refrigerator. A major branch moves to a fresh start image; returning to the main checkpoint restores the checkout image. Small actions in the same location keep the running video. Preserve the lesson DAG, voice session and checkpoint across video resets.

Scope: Web-first authored convenience-store demo using its original reference photo. Custom photos and Real world practice must never silently become demo store scenes.

Ownership:
- Backend agent: `portal/agent/scene-presets.js`, `service.js`, `schema.js`, context modules and regression tests. Server-owned allowlisted scene metadata, request opt-in validation, replay-safe scene selection and off-camera visual constraints.
- Web agent: `portal/public/learn.js/html/css`, new scene transition module/tests. Preload preset references, crossfade via new still image, reset/reseed/start the existing video session, gate video reveal on new output, cancel safely on Leave. No new dependencies.
- Root: `portal/public/scene-presets/**` assets/provenance, `portal/voice/routes.js` and `portal/voice-worker/config.mjs` scene binding plus tests, documentation, provider semantics, E2E verification, integration/PR.

Do not edit Unity, XR, ProjectSettings or existing editor-generated changes. Original image generation is user-authorized. No new packages. Record command acceptance separately from visual/frame evidence; do not claim perfect identity or lip sync. Failure must not silently reveal stale video as the new scene.
