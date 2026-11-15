# Unity startup and start-frame handoff

Branch `codex/demo-unity-startup`. Base PR #15 at `3e02168`. Covers the Unity and media parts of #29 and #35.

Owned files: `tools/android-dev/DevLessonControls.cs`, `tools/android-dev/stage.sh`, `Assets/Scripts/Learning/LearningAgentClient.cs`, `Assets/Scripts/Visko/ViskoSession.cs`, `Assets/Shaders/ImmersiveVisko.shader`, and this brief. The phone owner supplies prepared JPEG bytes through the existing bridge. The official MR reference is read-only.

Show the selected start frame before connecting, gate the lesson on an accepted opening turn and a valid new frame, and retain actionable Retry/Back on startup failure. Preserve the native stream setting and crop to a fixed 16:9 surface in the development app. Verify with the staged Android build, then test on the shared S25 and glasses when reachable.
