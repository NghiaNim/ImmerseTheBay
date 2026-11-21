# S25 development build

The developer APK uses the isolated XREAL template project at
`Builds/helloMR-baseline`, based on upstream XREALSDKTemplate commit `91806e6`.
The working vendor reference project and APK are separate. The dev scene is
generated from the sample with the XREAL hand rig, the practice screen,
subtitle and button UI, and horizontal plane detection.

Use Unity 6000.0.85f2 with Android support. The isolated template retains XRI
2.6.5 for its hand rig and uses the local XREAL 3.1 tarball. See the S25
implementation brief for template migration notes. Configure the local
`Assets/StreamingAssets/visko.json` in the isolated project for the reachable
portal. Enable the isolated project's custom main Gradle template and Android
internet access. For a local HTTP portal, allow HTTP in its Player Settings.

Run `tools/android-dev/stage.sh` to refresh the app sources in the isolated
project, then run Unity with `-projectPath Builds/helloMR-baseline -buildTarget
Android -executeMethod DevReferenceBuild.Build`. The APK is written to
`Builds/official-hellomr/ImmerseTheBay-XRI-live-dev.apk`.

Open **ImmerseTheBay Dev** in the Android launcher. The same app's home screen
works without the glasses: take a rear-camera photo, review it, enter or dictate
a goal, then tap **Upload and create practice**. The photo and goal go to the
configured Portal's existing practice endpoint. Connect the glasses, open
**Open selected session in glasses**, then tap **Start session** in the controller. The picker lists bundled lessons and the five most recent unexpired photo practices.
The pending photo stays in memory in the app and is lost if Android stops the
process. The separate official XREAL reference does not include this screen.

In the glasses, looking down gradually fades the video while keeping the
tabletop controls available. Looking up brings the video back.

## Fresh Mac setup notes (verified 2026-11-15, Unity 6000.0.85f2 on Apple Silicon)

- Install Rosetta 2 (`softwareupdate --install-rosetta`); Unity 6 refuses to start without it even though the editor is arm64.
- Copy the shared `~/.android/debug.keystore` from the previous build machine so new APKs update the installed app in place (installed signer SHA-256 `17868e3a…7c18`).
- Copy the upstream template into `Builds/helloMR-baseline`, put `com.xreal.xr.tar.gz` at its root, set AR Foundation to 6.0.7, create `Assets/Editor`, then run `stage.sh`.
- Set `ProjectSettings/ProjectVersion.txt` to `6000.0.85f2 (b028303d8e7d)` and pin `com.unity.xr.interaction.toolkit` to `2.6.5`. Otherwise every batch open re-runs the 2022→6000 upgrade, which bumps XRI to 3.0.11 and breaks the 2.6.5 samples.
- Delete `Assets/TextMesh Pro/Examples & Extras` (2022 example scripts do not compile on Unity 6; nothing in the dev scene uses them).
- Add `Assets/Plugins/Android/gradleTemplate.properties` (Unity's default template) with `android.uniquePackageNames=false`: AGP 9.1.1 rejects the XREAL `nr_loader`/`nr_common` AARs sharing the `nrsdk.pack` namespace.
- Enable Internet permission and HTTP for the LAN Portal before building (`PlayerSettings.Android.forceInternetPermission`, `PlayerSettings.insecureHttpOption = AlwaysAllowed`).
- After every build: `PYTHON=<python 3.11+> tools/android-dev/patch-dev-apk.sh <built.apk> <out.apk>` reapplies the Air 2 Ultra display patch (AGP 9's register layout is supported) and signs with the debug key; install with `adb install --no-incremental -r`.
- With the glasses attached, `adb exec-out screencap` needs `-d <phone display id>` from `adb shell dumpsys SurfaceFlinger --display-id`.

## Phone workflow (aligned with the web app)

Take a photo, choose 中文/English/日本語 and hold **Hold to say your goal** (or tap to start, tap to finish). The recording goes to the Portal's `/api/agent/transcribe`, the same server transcription as the web app, and the practice is created automatically as soon as both a photo and a goal exist, in either order. Typing a goal and **Create practice** remain as a fallback. **Start in glasses** opens the selected session; built-in lessons are under **Or choose a session**.

## Record the glasses and phone for the demo

Use wireless ADB with the glasses attached. Install host Python 3, `ffmpeg` with `libx264`, and `ffprobe`. Put `adb` on PATH or set `ADB` to the Unity Android tool. Set `ADB_SERIAL` to the paired S25's current wireless serial if more than one device appears. Choose the physical display by name from the listing each time the device reconnects.

```sh
python3 tools/android-dev/capture-demo.py displays
# Fill these in from the listing above.
AIR2_DISPLAY='paste-air-2-ultra-id'
PHONE_DISPLAY='paste-s25-id'
python3 tools/android-dev/capture-demo.py record "$AIR2_DISPLAY" ~/Downloads/tenin-demo/glasses-raw.mp4 --view glasses --seconds 120
python3 tools/android-dev/capture-demo.py record "$PHONE_DISPLAY" ~/Downloads/tenin-demo/phone-raw.mp4 --view phone --seconds 120
python3 tools/android-dev/capture-demo.py export-eye ~/Downloads/tenin-demo/glasses-raw.mp4 ~/Downloads/tenin-demo/glasses-left.mp4
python3 tools/android-dev/capture-demo.py inspect --decode ~/Downloads/tenin-demo/*.mp4
```

The Air 2 Ultra capture gives a 2560×720 side-by-side view of the application. It excludes the physical room and the phone’s own UI. The controller capture is 720×1560 portrait. Keep the phone orientation still throughout each take. `export-eye` crops the left half to 1280×720 by default and accepts `--eye right`. Check the crop for correct eye, upright captions and an unclipped frame before using it. Each take has its own clock. Record matching lesson beats for edit points and keep the originals outside the repository. ADB’s recorder produces silent video. [Android screenrecord documentation](https://developer.android.com/tools/adb#screenrecord)

Run **Start in glasses**, wait for the live scene, and start the glasses recording from the Mac. Capture the three scenes, one correction and the completion view. Repeat the lesson for the controller view with the phone’s notifications out of the way. The tool prints the temporary phone path and keeps it available if the Mac copy or decode fails. It removes that copy only after validating the local file.

### Check XREAL FPV with audio

Use a separate copy of the development project for this device experiment. The XREAL 3.1 manual shows a recording toolbar near the top of the view. The shipped Camera Features sample puts the rig in `FirstPersonStreammingCast/FirstPersonViewStreamingCast.unity`. Copy the sample rig or use the matching prefab when present. Use **VirtualOnly** for Air 2 Ultra and **ApplicationAudio** for the first test. Build, launch, accept Android capture permission when prompted, aim at the toolbar, and use **Record** to start and finish. Look for the file named `Xreal_Record_*.mp4` in the recording app’s `Application.persistentDataPath` or the gallery copy. [XREAL First Person View](https://docs.xreal.com/Tools/First%20Person%20View) · [Device compatibility](https://docs.xreal.com/XREALDevices/Compatibility)

The installed 3.1 sample scene selects **ApplicationAndMicAudio** even though the script’s default is **None**. **ApplicationAudio** asks for Android recording permission and MediaProjection consent. Unity’s Cartesia and feedback playback and the Kotlin Visko ambience use separate playback paths, so listen for all three in the exported clip. A listed audio stream alone establishes only that the file has an audio track. Microphone and mixed capture also need a learner-mic test because recording may contend with recognition. Use the command below to check the saved file when you have its exact path. [Android playback capture requirements](https://developer.android.com/media/platform/av-capture#capture_audio_playback)

```sh
"${ADB:-adb}" -s "$ADB_SERIAL" pull /path/to/Xreal_Record_123.mp4 ~/Downloads/tenin-demo/fpv-audio.mp4
python3 tools/android-dev/capture-demo.py inspect --decode ~/Downloads/tenin-demo/fpv-audio.mp4
```

Previously captured S25 files decoded as 13.98 seconds of Air 2 Ultra side-by-side picture and 9.99 seconds of phone controller picture. Both were H.264 with no audio stream. The offline exporter and inspector were checked against those actual files. SDK FPV audio, concurrent learner recognition, and a full three-scene take still need a device pass.
