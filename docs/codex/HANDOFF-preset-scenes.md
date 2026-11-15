# Preset demo scene transitions

Issue: https://github.com/NghiaNim/ImmerseTheBay/issues/13

Open http://localhost:8793/learn.html. Choose the original Home convenience-store scene and start. **Sandwiches**, **Drinks**, spoken shopping goals, and **Return to main** use the same lesson branch/rejoin flow. **Practice preview** exercises the images/fades without video generation. Real world and custom photos do not opt into this demo scene set.

## Behavior

- Checkout uses the existing original reference. Sandwich and drink shelves use two newly generated original unbranded images in `portal/public/scene-presets/`. Prompts/provenance are in `SOURCES.md` there.
- A scene change fades to the new reference photo, resets video in the same Reactor session, uploads/sets the new start image, reapplies visual/ambient prompts and resolution, then starts a fresh generation. The Cartesia connection, lesson run/revision and saved main checkpoint remain intact. Small steps within a scene do not reset video.
- Shelf views have no visible people: the clerk speaks off-camera. Returning to checkout restores the original clerk reference. All dialogue remains separate Cartesia audio; generated video supplies ambience.
- Fade-out waits for a generation-start boundary, first emitted chunk (zero-based, or first one-based chunk after the correlated start acknowledgement), then a newly submitted video frame. If prompt telemetry exists, it must match. Current deployments may omit it. This is stream-readiness evidence, not per-frame identity or lip-sync proof. Timeout/Leave cancels the gate; stale video must not be presented as successful switching.
- Server-owned scene metadata is excluded from model-controlled output schemas. Opt-in locks for the run. Replay cannot repeat transitions, and failed backend commits roll back scene state.

## API semantics and verification

The [official Dynamic schema](https://docs.reactor.inc/model-api-reference/visko-orbis-dynamic/schema.md) states that changing `set_image` during generation only takes effect after reset/new start. Reset clears prompt/image, while resolution/audio settings survive. The implementation still reapplies the necessary generation settings explicitly.

177 unit tests and syntax checks passed. Preview E2E passed checkout → sandwiches → another same-scene step → drinks → checkout, with revisions 1–5 and the original target restored. Stale Home-load regression also passed. Run `portal/agent/e2e-preset-scenes.cjs` with optional `--live`, using `PLAYWRIGHT_MODULE` and `CHROME_PATH` for the local browser installation.

Initial live attempt produced video chunks but the reveal gate timed out; correlated-start handling and first-chunk compatibility were corrected and tested. A subsequent attempt was blocked before allocation by Dynamic capacity after bounded retries. Final real-provider transition verification is recorded below when available; do not equate mock/preview success with verified video switching.

Unity/XR/settings and the APK are unchanged by this Web slice. The original generated checkout asset is retained.
