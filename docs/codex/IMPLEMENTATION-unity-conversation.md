# Unity conversational demo parity

Implement the current Web demo in Unity using existing portal state and Cartesia managed voice sessions. Preserve native glasses resolution, fixed first-person framing, dim static peripheral image and feathered central video. Use a compact subtitle/control layer appropriate to limited glasses FOV.

## Ownership for this task

- Unity conversation transport: new Assets/Scripts/Learning/Voice/** only. PCM microphone, server-minted Cartesia session, WebSocket stream, playback cancellation and client-tool events; no provider keys on device.
- Unity lesson/state integration: LearningAgentClient.cs, LearningContract.cs, Editor/LearningContractChecks.cs and Learning README. Bounded state, replay metadata, branch/return methods, authoritative connection/disconnection reconciliation; connect voice component at runtime.
- Unity overlay/presentation: LearningOverlay.cs and related new presentation files inside Assets/Scripts/Learning only. Compact wide layout, conversation/return controls, route/clerk status. Coordinate public methods with integration owner.
- Root: additive MicrophoneCapture.IsCaptureRequested ownership query (pending permission/start counts as ownership), documentation, Editor/LearningVisualChecks.cs (unsaved preview scene only), compile/build verification, Web joint E2E, minimal ViskoSession additive presentation hooks if needed. Never edit protected Scenes, ProjectSettings, package manifest or protected scene builders.

No new package dependency. Register module state with DebugRegistry; marshal network/audio callbacks to main-thread Update. Generate .meta through Unity import. Read existing classes and package APIs before implementation. Native Android microphone contention must be explicit; legacy speech and microphone capture may not run concurrently with conversation capture.

## Verification

Complete running real Web voice/video E2E independently. Compile Unity Android, run contract checks, and build APK if environment supports it. Automated compile does not demonstrate device mic/echo, gesture mapping or glasses FOV; document hardware limitations honestly.

## Implemented

The existing Learning object attaches CartesiaConversationClient at runtime, so protected scenes/builders need no edits. Voice uses server-created scoped sessions, mono PCM at 44.1 kHz, bounded queues, explicit interruption flushing and main-thread tool dispatch. Legacy microphone capture is suspended/restored. No AEC implementation is claimed. Native Dynamic remains mandatory; prompt commands are spaced by at least two seconds.

The client now understands branch routes, clerk speech and operation run/revisions. It serializes and deduplicates voice tools, discards interrupted unissued tool work, preserves already-issued decisions, and verifies snapshots before voice connection and after disconnect. An uncertain scene operation requires a safe restart. The overlay uses the lower 32% of the wide screen, with clerk text, primary learner sentence, Conversation and detour-only Return controls; secondary text collapses first.

## Verified evidence

- Real Web joint E2E succeeded in 51.469 seconds: Cartesia speech recognition/model/TTS plus real Dynamic, sandwich → water replacement → mainline rejoin. Three audio_output_clear events and two provider-confirmed interrupted assistant turns. Native 640×368 video advanced at four captured checkpoints (0.01, 7.21, 18.22, 30.78 seconds). Revisions 1–4, same saved return checkpoint, manual-mode reconciliation and clean microphone/video Leave asserted. Input was synthesized Japanese microphone audio, not a human acoustic test.
- Unity Android compile exited zero with no error CS; expanded LearningContractChecks passed.
- Actual Unity render produced Logs/unity-conversation-preview.png (1440×810) using the approved static photograph and real overlay/shader. Visual inspection confirms wide framing and compact controls. This is an offline design preview, not live Unity video.
- adb reported no connected device. Device voice audio, echo suppression, XREAL interaction and physical FOV remain unverified.

The ignored local visko.json points to the current developer portal on port 8793. Provider keys remain only in portal/.env. The APK and Logs are untracked artifacts. Current Cartesia managed configuration supports Japanese/English, not Chinese voice. Fine facial identity and shelf geometry still drift in generated video despite stable overall framing; command success is not visual fidelity proof.

Final Android APK rebuild after microphone lifecycle fixes succeeded with zero errors. Actual APK size is approximately 252 MiB at Builds/ImmerseTheBay.apk. Unity/XREAL build hooks modified protected settings/assets and generated an SDK AAR; those build-only changes were restored/removed after verification and are not part of the commit.
