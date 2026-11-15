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
