# ImmerseTheBay demo integration checkpoint

November 15, 2026. This handoff covers the integration status and the S25 validation procedure for master issue #16 and PR #15.

## Pinned build and verified results

| Component | Version or evidence |
|---|---|
| Unified-flow base | PR #15 at `3e02168c69274de99b106d8557f9beb2c41e661b` |
| Integrated source and Portal | `c083eb2264aa0455a34bb6c136643c5045ce30f3` on `codex/demo-integration` |
| Portal offline | `npm run check` and 212 tests passed in an isolated archive of the pinned source and lesson assets |
| Android compilation | Unity 6000.0.85f2 development build reported `XRI_DEV_BUILD_RESULT=Succeeded ERRORS=0` |
| Signed S25 APK | `ImmerseTheBay-s25-c083eb2.apk` · SHA-256 `d317a15207cffef2c2d232fa5b8204caf8f7aa36bae628031aeeeb430dd21ca3`. The display patch, debug signature and ZIP alignment passed verification. |
| Phone preview | Exact-run summary compiled in the standalone Tenin Android preview |
| Hardware | Wireless ADB listed no S25. The integrated build still needs S25 installation and wearer validation |

Current source gives photo lessons a common 16:9 JPEG from capture through preview and generation, shows the start reference until native video is ready, and keeps Retry available on startup failure. Ending a lesson opens the learner’s own saved run by ID. New records keep enough lesson metadata for replay after lesson expiry and Portal restart. The backend prepares exactly three generated exchanges. Captions use larger fixed text sizes. Native scene sound has an independent mute and is quieter during clerk playback. Cartesia adapters accept the pinned voice choice once that choice arrives from scene preparation.

The three-turn profile accepts explicit levels. Voice entry still supplies N4, so the phone default needs its own change to use N5. Scene preparation still needs to resolve and persist the partner voice and binding for the scripted playback route. Native gain settings are initial values for device calibration. Completion audio, score/progress parity, genuine streaming dictation, capture and preload remain open.

## Build and install the dev app

Use the isolated `Builds/helloMR-baseline` development project described in [phone + glasses drop-off](HANDOFF-dropoff-2026-11-15.md). Keep the official SDK 3.1 reference available as a separate installed app and project. On a machine with Unity 6000.0.85f2, stage from the pinned checkout.

```sh
ITB_DEV_PROJECT=/absolute/path/to/Builds/helloMR-baseline bash tools/android-dev/stage.sh
/Applications/Unity/Hub/Editor/6000.0.85f2/Unity.app/Contents/MacOS/Unity \
  -batchmode -nographics -projectPath /absolute/path/to/Builds/helloMR-baseline \
  -buildTarget Android -executeMethod DevReferenceBuild.Build -logFile /tmp/itb-demo-build.log
APKTOOL_JAR=/absolute/path/to/apktool_3.0.3.jar PYTHON=python3 \
  bash tools/android-dev/patch-dev-apk.sh \
  /absolute/path/to/Builds/official-hellomr/ImmerseTheBay-XRI-live-dev.apk \
  /absolute/path/to/Builds/official-hellomr/ImmerseTheBay-s25-c083eb2.apk
```

The development project needs Internet and development LAN HTTP enabled and a reachable Portal address in its bundled `visko.json`. Run the Portal from the pinned checkout with its ignored server configuration. Pair wireless ADB while the glasses occupy USB-C. Before an in-place install, compare the APK certificate with the shared debug signing certificate used by the existing installation, then use `adb install --no-incremental -r <signed-apk>`.

## First device pass

1. From Tenin, take a portrait photo with visible alignment cues. Confirm it crops centrally to 16:9 and the same still persists through scene preparation until the live video appears. Confirm right side up, proportional geometry and feathering in both eyes.
2. End two sessions with the same lesson title, then start another review session. Confirm each summary opens the exact run, an early exit is labeled as ended, and a saved run can replay after a Portal restart.
3. Compare captions in default pose, while turning and after connection recovery. Read Japanese, romaji and English from the normal wearing position. Confirm the phone still has working controls.
4. Verify audible clerk speech over quiet ambience and the Scene sound toggle. Start a spoken answer while scene audio is on and confirm recognition still controls the temporary mute. Check the two types of voice playback separately.
5. Run the remaining end-to-end checklist under #16. Keep provider logs, exported recordings and wearer observations separate from automated results.

## Partner voice checkpoint

| Component | Version or evidence |
|---|---|
| Portal and lesson prompt | `f9f140e4e48bf3a5c5067e1db9af987e4e942135` |
| Android source | `6790651` · Unity/Android build succeeded with zero errors. A subsequent Portal-only prompt change does not affect the APK. |
| Signed S25 APK | `ImmerseTheBay-s25-6790651.apk` · SHA-256 `0ebf89c341cb69263cab45ab1b8b78fa1435d2b62e3595df48662edd8d2e2b33`. Display patch, signature and ZIP alignment verified. |
| Automated verification | Portal check and 214 tests passed. Tenin standalone preview compiled. |
| Real providers | Cartesia returned two distinct Japanese WAVs for the same clerk line using Haruka and Hiroshi. Live scenario generation with a male override and live photo generation from the bundled original checkout still with Auto each returned three N5 exchanges. |
| Hardware | ADB listed no S25. Playback and casting continuity remain to be heard and seen on the device. |

Use this checkpoint and its APK instead of the earlier build above for voice testing. Set the two voice IDs from `portal/.env.example`, restart the dev Portal with the pinned source, and install the signed APK over the matching debug build. Tenin applies the voice selected on the Live Lesson screen during generation and displays the resolved voice on the glasses setup screen. Open a photo-generated lesson and a voice-generated lesson with different choices. Compare the opening lines, retry and branch return, and replay from Review.

Managed Cartesia conversations require both `CARTESIA_AGENT_ID_FEMALE` and `CARTESIA_AGENT_ID_MALE`. These were absent from the local config, so the verified audio path is scripted clerk speech. Live dictation, FPV capture and the rest of the master acceptance sequence remain tracked in #16.

## Shared sentence results checkpoint

Portal and Android source `94868e3d4552bcbf0637043cb04aa2562b170333`. Portal check and all 215 tests passed. Unity Android build reported zero errors. The display patch, signing, and ZIP alignment passed for `ImmerseTheBay-s25-94868e3.apk` (SHA-256 `59d7e54d9a065b724451be6c422ead8c56ab9b9fba46a45aa2f6c1974d93fb3c`).

Phone and glasses consume the same committed sentence result. Confirm a wrong answer shows Try again, then open an explicit Hint and speak the right target to see Matched with a hint. For a successful attempt, read the result next to the previous target even after the next line appears. Compare both displays immediately after the response and check that the same result disappears after six seconds. Verify Next shows Skipped without a percentage. In a side trip, the main step and completion bar must remain unchanged while side-trip progress advances. The Portal regression covers those semantics and one exact lost-response retry. Timing and readability still need a wearer.

At the end of this checkpoint S25 appeared over USB with ADB status unauthorized. Unlock and accept the device prompt before installing with USB. Enable or reconnect wireless debugging before plugging the glasses into USB-C. Keep the official reference available to compare the per-eye display.

## Tenin entry and scene preview checkpoint

Android and Portal checkout `1adb8a17aa397a43b1ca0b2c00208c82ae55e814`. This Android UI update leaves Portal code at the previous 215-test checkpoint. The standalone Android preview and Unity development build both succeeded, with zero errors reported by Unity. The display patch, signature and alignment checks passed on `ImmerseTheBay-s25-1adb8a1.apk` (SHA-256 `e756627ddebade5599b221b3f7474734a58a63bcf5193a910b7580ae43ecb8ae`).

The phone home screen uses single-row action cards and the Live Lesson uses one visual style for camera, library and spoken goals. The photo disclosure explains the lesson service's 30-minute in-app window. The scenario list and detail show center-cropped views of their actual bundled seed. Both the preview and production APK contain byte-identical convenience-store and home seed assets at the paths the lesson JSON names. The preview loader decodes locally off the main thread and assigns each result to the view for that exact bundled path. Missing bundled previews leave a neutral icon and do not prevent navigation.

On the S25, open the app at default system font size and confirm the home prompt fits a single line. Raise system font size and confirm it wraps without clipping. Switch quickly between home and convenience-store scenario detail and confirm the images never cross. Repeat offline. On both lesson types, compare the preview composition with the preparation still in the glasses. The phone was still awaiting a reachable Wi-Fi ADB connection at the end of this checkpoint.

## Wearer feedback batch

Source and running Portal at `4c8819b`. Portal syntax check and all 224 tests passed. Unity Android build reported `XRI_DEV_BUILD_RESULT=Succeeded ERRORS=0`. The S25 installed `ImmerseTheBay-s25-4c8819b.apk` and returned the expected SHA-256 `12f82c318d6374ebd4f3f65192b42cda1d9594aeac44d37226678bde2fd4f3fe`. The display patch, signer and 16 KiB ZIP alignment checks passed. The phone reached the pinned Portal over Wi-Fi and its scenario detail showed the curated demo and Japanese, romaji and English in Key Phrases.

The convenience-store catalog entry now opens three authored targets, at the sandwich shelf, refrigerated drinks, and checkout. JavaScript tests verify the order, a detour followed by return to the saved drinks scene, and the three-target completion count. Unity caches one likely next bundled image and reseeds Dynamic with the new image on transition. The device must still prove the first usable live frame and latency on each transition. This version warms the image only. Dynamic starts generating after the scene changes.

To test the new build, leave Voice example on, wear the glasses and start the convenience-store lesson. Confirm the clerk and example are distinct and audible, the microphone activates after the example, and the bottom-right input waveform follows speech. Speak the first target and look for the immediate Good job or Please try again indicator on the phone and in glasses. Continue to the drinks scene, take a detour, return to the drinks scene and finish at checkout. Check the eye-level summary and compare it with phone Review. Try with Voice example off, restart the app and verify that its setting persists. Check the wider fade over surfaces at different room brightnesses and try selecting each raised control with both hands.

Earlier on-device footage was captured before this update. The wearer confirmed picture and clerk audio, with audio heard only in the left ear. The available Cartesia WAV fixtures are mono, and Android showed both stereo and mono audio devices. Isolate the headset route and compare the new stereo cue, clerk voice and guide before attributing the fault. ADB `screenrecord --display-id` captured 14 seconds of stereo glasses render and 10 seconds of phone controller render. Both MP4s decoded on Mac with video and no audio stream. The single-eye editor copy came from the end of the glasses capture. Use the SDK audio-capable capture from #39 and #34 for a complete synchronized demo.

### Latest build for the consolidated wearer pass

Running Portal and S25 source `600d827`. Portal check and 229 tests passed. Unity Android build reported zero errors. Signed APK `ImmerseTheBay-s25-600d827.apk` has SHA-256 `3823105692d4feaec623cc5567aca84023d9fc8ee9c121d98a578aa77c491644`, confirmed against the APK on the S25. The installed app reached the pinned Portal. The served and packaged lesson has three targets and the corresponding sandwich, cooler and checkout seed images. Track integration in draft PR #42.

This build adds word-aligned kana and romaji for the three reviewed targets, a localized retry focus when a recognized transcript provides evidence for one differing word, and stereo output for the scripted clerk and spoken examples. The real Cartesia example route returned two-channel PCM16 at 44.1 kHz. Verify both ears, speaking order and audio levels on the same hardware. The wearer requested a consolidated device pass after the current changes are finished.

The independent Quest prototype is in [srcJin/TeninQuest3](https://github.com/srcJin/TeninQuest3), `main` at `aa37fc4`. Desktop testing decoded a public SPZ test asset and played a feathered H.264 overlay. The updated World Labs key passed a read-only check. The Quest work remains offline-first, with a custom Marble world, live Dynamic video and Quest headset behavior awaiting verification.
