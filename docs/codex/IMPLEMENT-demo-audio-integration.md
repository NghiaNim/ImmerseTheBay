# Connect native ambience control to the development lesson

Issue #18. This integration slice owns only `Assets/Scripts/Visko/ViskoSession.cs`, `tools/android-dev/DevLessonControls.cs`, and this brief on `codex/demo-integration`. It consumes the native bridge added by `e35146a`.

Keep generated audio quiet by default, lower it further while the clerk speaks, and expose a scene-only mute on the Unity phone panel. Keep the existing recognition mute independent of volume and preserve the wearer's current scene setting across replay, retry and backend recreation. Verify a joint Android build. Actual perceived levels and device audio paths need S25 testing.
