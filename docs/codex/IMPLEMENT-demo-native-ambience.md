# Native scene ambience seam — #18

Branch: `codex/demo-native-ambience`, based on `3e02168`. Local commit only.
Owned files: `Assets/Plugins/Android/ViskoReactorBridge.kt` and this document.

## Result

Generated `main_audio` plays through Android `AudioTrack`, outside Unity's
`AudioSource` mixer. The bridge now accepts a bounded local scene gain without
changing Reactor generation or Cartesia dialogue. The existing `audioEnabled`
hard mute remains authoritative.

All desired-state updates, track volume/play/pause/flush operations, writes and
release use the existing `audioLock`. The legacy mute setter now changes its
backing field under that lock too, so competing mute/unmute calls cannot leave
the track in a state inconsistent with the stored flag.

## Exact Unity bridge contract

Kotlin instance method on `com.immersethebay.visko.ViskoReactorBridge`:

```kotlin
fun setSceneGain(gain: Float, revision: Long): Boolean
```

Java: `public final boolean setSceneGain(float gain, long revision)`.
JNI descriptor: `(FJ)Z` (verified with `javap -s`). Example C# call for the Unity
owner to add to `AndroidViskoBackend`:

```csharp
bool accepted = m_Bridge.Call<bool>("setSceneGain", desiredGain, revision);
// desiredGain must be System.Single; revision must be System.Int64.
```

- Finite gain is clamped to `[0, 1]`; NaN and either infinity are rejected.
- Revisions are nonnegative, strictly increasing, and shared by all gain callers
  for one bridge instance. Start at `0L`. Negative, duplicate and older revisions
  return `false` without changing gain or touching the track. Invalid gain does
  not consume a revision. Do not wrap the counter or reset it on reconnect.
- `true` acknowledges acceptance of desired state, not successful sound output.
  This is a synchronous local call, not a Reactor command or main-handler post.
- Default gain is `1f` for compatibility. Gain can be set before connection or
  before the first audio frame; it does not create an audio track.
- Effective gain is `audioEnabled ? sceneGain : 0`. The existing Java
  `setAudioEnabled(boolean)` / JNI `(Z)V` signature is unchanged. A gain change
  cannot unmute it. Re-enabling restores the latest desired gain, including zero.
- Zero effective gain sets track volume to zero, pauses and flushes queued PCM.
  Incoming audio frames are dropped until both gates allow playback. Restore
  applies gain before play; a newly created track gets that gain before its first
  play/write. Audio lost while muted is never replayed.
- Track-control exceptions or a failed `setVolume` result trigger stop/release
  and clear track ownership. Subsequent eligible frames can create a fresh track;
  the failure path does not continue writing to a track at an outdated gain.
- Disconnect releases the track but preserves gain, mute and revision on this
  bridge instance. Old-connection frames are still rejected by Reactor identity.
  A new bridge starts with new defaults. Once disposal executes, gain calls return
  `false`, legacy mute writes are ignored, and frames cannot recreate a track.

## Requests for owners

### Unity / lesson audio owner

1. Add the method through `IViskoBackend`, `AndroidViskoBackend` and `ViskoSession`.
   Unity audio-source volume alone cannot control this native scene track. Keep
   one scene-audio policy owner and one revision counter per bridge; retain the
   desired policy in Unity and apply it before connection/first frame whenever a
   backend is recreated. Surface desired gain/mute in the existing HUD.
2. Compose normal ambience, Cartesia ducking, microphone hard mute, explicit user
   mute and lifecycle state in that owner. Use the latest aggregate desired gain
   when an operation ends, rather than restoring a captured scalar blindly. Keep
   `PortalSpeechToText` and `AndroidSpeechToText` mute ownership coordinated;
   `setAudioEnabled` remains an unversioned compatibility gate, not a multi-owner
   lease system. A stale legacy `true` call is not rejected by the gain revision.
3. Give async speech/download/listening work an ownership generation. Ignore
   completion/cancellation from superseded generations. Allocate a gain revision
   when the owner accepts a desired-state change and carry that same revision
   through delayed delivery. Do not mint a newer revision in an obsolete callback
   or reserve a future restore revision in advance. The native setter rejects
   older delivered states; it cannot infer whether a new revision came from a
   stale lesson callback.
4. Wire duck/restore around the actual Cartesia playback in `LearningAgentClient`
   and the conversation path, with cleanup on errors, cancellation, next turn,
   Leave, component disable and backend recreation. Keep microphone/user mute
   authoritative throughout. Choose the normal/ducked levels in Unity policy;
   the bridge deliberately introduces no product-specific levels or fades.
5. Add Unity contract/lifecycle tests for overlapping speech and mic ownership,
   late callbacks, Leave/restart and pre-connect policy replay. Then perform the
   integration compile and device listening checks under the integration owner's
   authorization. No C# or scene wiring is included in this commit.

### Portal / content owner

Keep generated audio prompts environmental-only and Cartesia responsible for
spoken dialogue. Retain/extend prompt-contract tests for all start, branch,
rejoin, photo and replay paths. A prompt cannot guarantee the generated stream
contains no voices; this gain controls the whole generated track, not source
separation. Any server/browser policy or UI change belongs to the Portal owner.

## Isolated verification

Completed without opening Unity, running a device command or starting/calling
Portal, Reactor or Cartesia services:

- Compiled the complete modified Kotlin file using cached Kotlin 2.2.10,
  coroutines 1.10.2, Android API 35 `android.jar`, and Reactor Android SDK 1.1.0
  `classes.jar`. Exit 0; no new dependency or package/configuration changes.
- Inspected the compiled class with `javap -s`; verified `(FJ)Z` and unchanged
  `(Z)V` legacy mute ABI.
- Compiled that same complete source against in-memory Android/Reactor test
  doubles and ran 12 JVM behavior tests. The harness uses reflection only to
  inject current/old Reactor identities and deliver media callbacks; it never
  opens a connection. Cases: default playback order; pre-track gain/clamping;
  invalid gains/revisions and stale/duplicate restore; zero-gain flush/drop and
  fresh restore; hard-mute precedence/latest-gain restore; mute before first
  frame; disconnect/replacement and old-frame rejection; existing-track volume
  failure; new-track volume failure before play/write; pause failure while muted;
  disposal; concurrent gain/mute/media updates.
- The concurrency case repeats 25 rounds with six gain/media threads delivering
  600 revisions out of order and a competing mute thread. Each round converges to
  revision 599, remains silent under mute, then restores that revision's gain.
- `git diff --check` passed. Only the two owned paths changed; no new Unity asset
  was added, so no new `.meta` is required.

The temporary harness/build outputs are outside the repository and are not part
of this commit. These checks establish Kotlin/API compatibility and bridge
state behavior with test doubles. They do not establish actual AudioTrack
latency, speaker silence, perceived levels, Unity JNI integration or hardware
behavior. Unity/APK builds and device/audio acceptance were not run, as directed.
