# Record S25 and Air 2 Ultra demo footage

Issues #34 and #39. Branch `codex/demo-record-export`, base `600d827`. Own `tools/android-dev/capture-demo.py`, the capture section of `tools/android-dev/README.md`, and this brief.

The host tool selects an explicit physical display, validates a local copy of the recording before removing the temporary file, and supports a one-eye editor export. It leaves the installed apps and the official reference project in place.

## Device acceptance

- Confirm that the main phone panel and the Air 2 Ultra are correctly identified from their current physical display entries. Record a full three-stage lesson with the glasses, then separately repeat the same steps on the phone for inserts. Keep both original captures.
- Play back both files and confirm text and controls are visible. Open the one-eye crop and confirm the intended lens, upright text, and visible edges.
- In a separate development project, record the same lesson with the official FPV rig under VirtualOnly / ApplicationAudio first. Verify clerk, example and feedback cue independently. Check native scene ambience separately because it uses Android AudioTrack. Compare the wearer experience with the rendered file.
- Try the SDK mic and mixed modes separately. Verify the learner's transcribed answers and recording audio together before choosing this configuration. Preserve an uninterrupted take with the silent recorder if the FPV mic competes with recognition.

## Requests for owners

The build owner can wire the official FPV rig into a separate development-only scene once recording and microphone coexistence have been verified. The SDK sample uses `Camera.Render()`, so verify the capture camera on the selected render pipeline before assuming compatibility. The existing XREAL sample project remains the reference.

## Verification

- The host script passed Python compilation and `git diff --check`.
- A mocked ADB run exercised explicit display selection for both outputs, unchanged original bytes after pull, validation before device-side cleanup, missing display rejection and retaining a corrupt capture for recovery.
- FFmpeg decoded both previously recorded S25/Air 2 Ultra MP4 files in full. The left and right exports each contained all 840 video frames at 1280×720. No audio stream was present. The exporter rejected the portrait phone file as an eye source and refused to overwrite an existing export.
