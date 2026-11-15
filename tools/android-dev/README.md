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
