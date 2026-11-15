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
