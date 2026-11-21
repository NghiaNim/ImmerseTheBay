# Unity startup and start-frame handoff

Branch `codex/demo-unity-startup`. Base PR #15 at `3e02168`. Covers the Unity and media parts of #29 and #35.

Owned files: `tools/android-dev/DevLessonControls.cs`, `tools/android-dev/stage.sh`, `Assets/Scripts/Learning/LearningAgentClient.cs`, `Assets/Scripts/Visko/ViskoSession.cs`, `Assets/Shaders/ImmersiveVisko.shader`, and this brief. The phone owner supplies prepared JPEG bytes through the existing bridge. The official MR reference is read-only.

Show the selected start frame before connecting, gate the lesson on an accepted opening turn and a valid new frame, and retain actionable Retry/Back on startup failure. Preserve the native stream setting and crop to a fixed 16:9 surface in the development app. Verify with the staged Android build, then test on the shared S25 and glasses when reachable.

## Native operation deadlines

The Android bridge includes queueing, provider acknowledgement, and the decoded-frame START fallback in a single 30-second operation budget. START samples the frame counter inside the command mutex. An expired operation closes the transport before queued commands can run against uncertain provider state. Unity uses a 35-second watchdog for missing bridge callbacks and invalidates the transport on that path too. Intentional session cancellation does not produce a command error.

An isolated coroutine harness exercised the actual bridge operation methods with substituted provider and Android callbacks. A late START reply followed by a missing frame expired under the common deadline, canceled a queued prompt, and reported one failure and disconnect. A frame delivered while START was still queued did not satisfy START. A new frame, explicit generation acknowledgement, and explicit provider rejection each took the expected path. The Android player build compiled and packaged successfully. Wearer validation under a real interruption is pending.
