# Demo phone preparation foundation

- Issues #17 and #35. Baseline PR #15 at `3e02168`.
- Branch `codex/demo-phone-prep`. Own `tools/android-dev/PracticePortal.kt`, `TeninWorkflow.kt`, and `tenin/{TeninActivity,TeninScreens}.kt` in the phone lane.
- Give camera and library one upright, center-cropped 16:9 JPEG. Use those bytes for the preview, photo-practice request, and existing Unity handoff.
- Show the prepared photo on the phone while capturing the goal, preparing the lesson, and getting ready to put on the glasses. Use the existing spinner for in-flight steps.
- Cancel recording and invalidate abandoned work on navigation. Preserve intentional camera, picker and permission activity results.
- Compile a standalone phone preview. Device checks and the Unity first-frame transition are coordinated separately. Streaming transcription and speaker selection are subsequent work.

## Verification

The standalone Android preview compiled and its APK signature verified. On S25, verify camera and picker with a portrait image containing a visible center marker. Compare its orientation and 16:9 framing on the goal, busy and launch screens. Press Back during recording, transcription and generation, then start a voice-only lesson and confirm that results from the canceled attempt never appear and no photo is reused. Open the camera or picker, return successfully or cancel, and confirm the intended action survives the temporary activity pause. Put the app in the background while recording and confirm it releases the mic and offers a fresh take on return.
