# Immersive media integration / PR handoff

`SpatialMediaPlayer` supports `MediaProjection.Flat` (default), `Dome180` and
`Sphere360`. Existing `Play(source, layout)` calls always select Flat. Use
`Play(source, layout, projection)` for immersive media, or set `projection` in the
Inspector with `playOnStart` enabled.

Immersive playback replaces the screen mesh with an inward-facing procedural
hemisphere/sphere, radius `radiusMeters` (default 10 m). Each Play call centres it
on `Camera.main` at that moment, levels the horizon and points the image centre
along the head's horizontal forward direction. It stays at that pose rather than
following the head. With no main camera it uses the object's existing pose.
Returning to Flat restores the screen mesh and its previous local pose, then
applies the normal media aspect ratio. Use uniformly scaled parents (as the
current content/anchor hierarchy does); rotated, nonuniform parent scales can
introduce shear.

Input is equirectangular: Dome180 spans 180 degrees horizontally and vertically;
Sphere360 spans 360 degrees horizontally and 180 vertically. The front (+Z) maps
to the image centre, right (+X) increases image U, and up (+Y) increases V. Mono,
SideBySide (left eye = left half), TopBottom (left eye = top half), and `swapEyes`
apply after projection. The existing StereoUnlit shader handles all modes and
keeps the instancing/multiview stereo macros. No additional shader inclusion or
package is needed.

Playback API: `Pause()`, `Resume()`, `TogglePause()`, `Seek(double seconds)` and
`SetVolume(float)`; read `IsPlaying`, `Duration` and `Time`. Time values are in
seconds and return zero for images/unprepared video. Seek clamps to the duration
and is ignored if the backend cannot seek. Pause and seek during preparation are
applied when preparation completes. Volume clamps to 0..1 and controls the video
AudioSource. The HUD reports projection, layout, swap, volume and playback state.

## Requests for owners (include in the PR description)

- Content owner: add `public string projection = "Flat";` to `ContentItem`, parse
  it as `MediaProjection` with Flat fallback, and call the three-argument `Play`
  overload in `ContentSpawner.BuildScreen`. No content files were edited here.
  The proposed `content.json` field is **`projection`**, with values **`Flat`**,
  **`Dome180`**, **`Sphere360`**. For example:

  ```json
  { "id": "panorama", "label": "Panorama", "type": "image",
    "source": "images/panorama.jpg", "layout": "Mono",
    "projection": "Sphere360", "size": 1.2 }
  ```

  `size` continues to describe a flat screen; immersive radius defaults to 10 m.
- Networking owner: subscribe/unsubscribe to
  `MicrophoneCapture.OnPcmChunk(short[] pcm16, int sampleRate)`. Callbacks occur
  on the main thread; enqueue transport work. Each chunk owns its array; do not
  mutate it. Encode shorts explicitly in the transport's agreed byte order.
  No NetClient reference or wire format is introduced here.
- Integration owner: import the new scripts/docs and commit Unity-generated
  `.meta` files, then run the project compile and visual checks below. Unity was
  deliberately not launched for this task, so new assets have no `.meta` yet.

## PCM behaviour and checks

`chunkMs` defaults to 40 and is applied at capture start. At 48 kHz this gives
1920 mono PCM16 samples per full chunk. The clip's actual sample rate and channel
count drive extraction; multiple channels are averaged before conversion. A
read cursor independent of the waveform window consumes each new frame once,
retains incomplete chunks across updates and handles reads spanning the clip
wrap. Multiple chunks can arrive in one Update after a hitch. StopCapture emits
a final short chunk for any remaining frames. Restart begins a fresh stream.

The native ring holds ten seconds. Polling must resume before unread data is
overwritten; samples lost during a longer main-thread stall cannot be recovered.
Such stalls resynchronise at the writer and increment the HUD's overrun count;
they do not replay old PCM. Consumers should treat stopping/restarting or an
overrun as a stream discontinuity.

There is no test framework dependency. Run **Tools > XREAL > Check Microphone
PCM Continuity** in the editor. It uses a synthetic stereo AudioClip and the
production `OnPcmChunk` event path, checks 4000 uniquely ordered frames across
15 wraps of a 257-frame ring, verifies main-thread delivery and actual sample
rate, and polls unchanged cursors to detect duplicates. Retained chunk arrays
are checked afterwards to catch accidental reuse. Pure reader checks additionally
cover mono/stereo, incomplete tails, failed reads, overwrite detection and PCM
clipping (including -1/+1 endpoints). No real mic or media asset is needed.

For visual verification, create a quad with a StereoUnlit material and
SpatialMediaPlayer, set `source` to an uncommitted local equirectangular image
with labelled front/right/back/left/top/bottom directions, set Mono and Sphere360,
then enter Play mode. Confirm front at startup, right when turning right, top
when looking up, and rear seam continuity. Repeat Dome180 (front hemisphere
only), Flat (original aspect/pose), SBS/TB with labelled eye halves and swapEyes.
For video, check pause/resume/toggle, seek below zero/beyond duration, volume,
and pause/seek while preparing. Repeat in the glasses for actual per-eye output.

Validation performed without launching Unity: standalone Roslyn compilation of
all Media/Audio C# scripts plus DebugRegistry against the installed Unity 6
assemblies passed with both UNITY_EDITOR and UNITY_ANDROID defines. The actual
PcmChunkReaderChecks suite ran under .NET and passed. `git diff --check` passed.
This does not compile shaders or execute Unity native APIs.

The full Unity project compile, editor menu execution, Play-mode visuals and
device checks are left to the coordinator as requested. No large media is committed.
