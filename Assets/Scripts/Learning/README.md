# Unity learning client

The client follows `portal/agent/contract.md`. The default lesson is
`ja-konbini-001`; authored lesson content remains owned by the content stream.

## Scene wiring (integration owner)

Create a Learning object with `LearningContext`, `LearningOverlay`, and
`LearningAgentClient` components. Assign:

| Component | Fields |
| --- | --- |
| LearningAgentClient | `visko`: shared ViskoSession; `context`: LearningContext; `overlay`: LearningOverlay; `speechProvider`: E's AndroidSpeechToText component |
| LearningContext | `headCamera`: XR camera; `microphone`: MicrophoneCapture; `meshManager`: ARMeshManager; `planeManager`: ARPlaneManager |
| LearningOverlay | `screen`: ViskoSession.screen; `headCamera`: XR camera |

The client assigns `context.viskoScreen` and `context.overlayPanel` at runtime.
The bundled Noto CJK font loads automatically; its license and upstream links
are in Resources. Keep the existing EventSystem/XRUIInputModule and enable UI
interaction on the hand/controller interactors. The overlay supplies its own
TrackedDeviceGraphicRaycaster and ordinary GraphicRaycaster.

Bind integration menu actions to `BeginLesson()`, `LeaveLesson()`, and optionally
`LearningContext.Point()`. The overlay contains only Speak, Hint, and Next.
`LearningOverlay.SetKanaVisible`, `SetTranslationVisible`, and
`SetChineseTranslation` can be bound to secondary settings controls.

For a sample image, set `LearningAgentClient.startImage` to its StreamingAssets
path. For a phone photo, await E's `PhotoCapture.CaptureJpeg()` and pass its bytes
to `BeginLessonWithImage(bytes)`. The client resets the shared scene and service,
then uploads the image and awaits acceptance before the initial agent turn.
Setting an image before calling `BeginLesson()` would reset away that image.
Beginning/restarting deliberately changes the scene for every joined client.

Add MeshCollider to the generated spatial mesh prefab. ARMeshManager maintains
its mesh. No plane collider is needed: LearningContext intersects plane boundaries
directly. The central video/environment feather material belongs to integration;
the overlay stays within the lower 45% of the video quad, with a 3% bottom margin.

## Behavior and limits

- Turn requests and native commands are serialized. The cursor advances and a
  learn event is sent only after all command acknowledgements. A failure requires
  Leave or a new Begin; requests are not blindly retried after possible server
  acceptance. Glasses learning rejects non-native resolution and never falls back
  to 1080p. The web's separately approved fallback does not apply here.
- Learn events render only, matched to the connected device session or announced
  portal session in the editor. They never execute Visko commands again. The hub
  does not replay past learn events: joining/re-enabling may need the next web
  update or an explicit Begin. Local input is disabled until a fresh target exists.
- Automatic short/long pinch intents require gaze on the video. Overlay UI owns
  its own pinches. Long pinch is at least 0.6 seconds and never also emits Next;
  lost tracking cancels the gesture. Multi-key click means Next, long press Hint;
  double click remains reserved for recenter.
- Point is an explicit action, using fingertip origin and approximate palm-forward
  direction because HandInput does not expose measured index direction. Unknown
  foreground surfaces occlude classified targets. SDK docs and sample disagree
  on face/vertex labels; ambiguous array layouts return unknown pending hardware
  verification. Desk height is geometry, not proof of a table classification.
- RMS/VAD is observational. E's speech adapter owns microphone suspension and
  restoration. Zero PCM RMS during Android recognition never ends speech here.
  Callbacks are marshalled to Update; old request results are discarded.
- EditorSpeechToText supports explicit transcript injection via its inspector
  context menu. Editor client rendering follows the web; native command driving
  requires Android READY. The stub does not claim to recognize audio.
- Scores are text similarity from the service, not measured pronunciation.

## Verification

Standalone compilation against installed Unity 6000.0.85f2 engine/package
assemblies passed for both UNITY_ANDROID and UNITY_EDITOR, using actual dependent
repository sources and E's ViskoSession (no API stubs). This does not replace a
Unity import/build or hardware validation.

Main owns Unity runs. After import, run `Tools > Learning > Check agent contract`
or batch `-executeMethod ImmerseTheBay.Learning.Editor.LearningContractChecks.Run`.
These offline checks cover null context fields, Japanese/JSON escaping, flattened
sync cursor, completion clearing, command allowlist, native-before-start, and
oversized transcript rejection. They do not contact the portal or Reactor.

No Unity process was launched in this workstream, so generated `.meta` files must
be committed by integration after import, including `Assets/Scripts/Learning.meta`.
World-space typography, XRI button interaction, speech permissions, classifications,
and device rendering still require the S25 + Air 2 Ultra check.

## Managed conversation parity

`LearningAgentClient` now adds `CartesiaConversationClient` at runtime; no scene migration or device API key is needed. Configure the portal URL/token on the existing Visko config. The portal must have its Cartesia managed agent configured. Conversation and Return controls are exposed by the compact overlay. `SubmitIntent(string)` supports spontaneous shopping goals, and `ReturnToMain()` rejoins the saved checkpoint without restarting video.

Lesson requests carry a unique request ID plus authoritative run/revision. Connection failures retry the identical body at most once; commands are claimed before sending and never retried after uncertain execution. Local operations are serialized and capped at 256 per lesson. Mirrored web events only render and update the cursor; they never execute video commands. Legacy events without operation metadata cannot authorize local controls.

Voice startup and return to manual mode compare the read-only server snapshot. A changed run/revision requires restart, without replaying unknown commands. Legacy speech is stopped before voice capture. Client tool calls are deduplicated (256 per call), queued up to eight, and serialized through the same lesson state. Barge-in discards queued old-turn tools while an already-issued lesson operation finishes. Leave/pause/disable cancel requests and voice resources. Device microphone, echo cancellation and glasses presentation still require hardware verification; compilation alone does not verify them.
