# Drop-off — phone + glasses app (2026-11-15)

Branch: `feat/unified-flow` (pushed). Start here, then [HANDOFF-tenin-phone-ui.md](HANDOFF-tenin-phone-ui.md) for the phone screens and [tools/android-dev/README.md](../../tools/android-dev/README.md) for building.

## What the app does today

One Android app (`com.immersethebay.app`) on a Galaxy S25 with XREAL Air 2 Ultra glasses:

1. **Tenin** (phone home screen) → **Live Lesson**:
   - **Photo**: take or pick a photo → "What do you need to do?" (spoken goal) → photo practice from `/api/practice/generate`.
   - **Say my goal**: spoken goal alone → a 3–4-line lesson written by `/api/scenarios`.
   - **Pre-made Lesson**: the two bundled lessons (convenience store, home).
2. **I'm wearing them** opens the glasses view. The lesson starts by itself, with no Start session tap.
3. **In the glasses**: generated video, the partner's line spoken by Cartesia, subtitles. After each line the glasses **listen on their own**; a match at **60%** or more (`PASS_SCORE`) moves on.
4. **Phone during the lesson** shows the Tenin "Lesson running" screen: a big round, animated mic circle (tap to speak any time), step progress, and **End lesson**.
5. **End lesson** returns to the same Tenin, which shows the summary. Review and Vocabularies read saved sessions from the Portal.

While Tenin is in front, the glasses show a dark Tenin screen with one message per eye, instead of Samsung's stretched mirror.

## Run it

```sh
# Portal (Mac). Keys in ignored portal/.env: REACTOR_API_KEY, OPENAI_API_KEY (+gpt-6-luna access), CARTESIA_*.
cd portal && npm ci && PORT=8793 node server.js          # 201 tests: npm test

# Phone build (Mac with Unity 6000.0.85f2 + Android module, Rosetta installed)
tools/android-dev/stage.sh
/Applications/Unity/Hub/Editor/6000.0.85f2/Unity.app/Contents/MacOS/Unity -batchmode -nographics \
  -projectPath "$PWD/Builds/helloMR-baseline" -buildTarget Android -executeMethod LocalDevSetup.Build \
  -logFile "$PWD/Builds/itb-android-build.log"                       # expect XRI_DEV_BUILD_RESULT=Succeeded ERRORS=0
PYTHON=.venv/bin/python tools/android-dev/patch-dev-apk.sh \
  Builds/official-hellomr/ImmerseTheBay-XRI-live-dev.apk Builds/official-hellomr/ImmerseTheBay-s25.apk
adb connect <phone-ip>:<port>      # Wireless debugging; pair first with `adb pair`
adb install --no-incremental -r Builds/official-hellomr/ImmerseTheBay-s25.apk
```

`adb` is Unity's: `/Applications/Unity/Hub/Editor/6000.0.85f2/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb`. The fresh-Mac quirks are in the dev-build README:
- Rosetta, and pinning XRI 2.6.5 with ProjectVersion 6000.0.85f2;
- deleting the TextMesh Pro examples;
- `android.uniquePackageNames=false` for AGP 9;
- the shared `~/.android/debug.keystore`;
- `LocalDevSetup.cs` (Internet permission plus cleartext HTTP), which lives only in the ignored dev project.

`Builds/helloMR-baseline` is ignored and must be recreated per machine from XREALSDKTemplate tag 3.1.0 (README). `Assets/StreamingAssets/visko.json` in it holds the Mac's LAN IP. Rebuild when the IP changes.

## Device facts (S25, Android 16)

- **Display patch is required after every build.** The XREAL runtime does not recognise the display name `Air 2 Ultra`. The installed ControlGlasses is a locally patched build (`8e1f7e82…`); leave it alone. `patch-dev-apk.sh` patches our APK (only `classes.dex`) and signs it with the shared debug key (`17868e3a…`), so updates install in place.
- **Plugging in the glasses opens Control Glasses briefly.** It is the default app for the glasses' USB device and starts the runtime, then you return to Tenin. Expected.
- **Samsung "Connected display → Mirrored" is required.** Any ordinary phone screen is therefore stretched across both eyes unless we draw on the glasses ourselves. Tenin does this with `TeninGlassesScreen` (Android `Presentation`). The XR view must stay in front for the whole lesson: moving Tenin to the front during a lesson made the glasses fall back to the mirror.
- **One task.** Tenin, the old phone screen, NRXRActivity and UnityPlayerActivity all use `taskAffinity="xreal.unity"`, so there is one entry in Recents, and `REORDER_TO_FRONT` reuses the same Tenin.
- **Battery.** Below about 15% the phone cuts power to the glasses. Charge before testing.
- **Wireless debugging port changes** when debugging restarts. University Wi-Fi allowed phone↔Mac traffic in our tests.

## Code map

| Area | Files |
|---|---|
| Phone UI (Tenin) | `tools/android-dev/tenin/{TeninUi,TeninScreens,TeninActivity}.kt`, mockups in `Tenin_phone-hifi/` |
| Phone workflow | `tools/android-dev/TeninWorkflow.kt` (Live Lesson hooks), `PracticePortal.kt` (photo encoding, Portal calls, glasses hand-off), `TeninGlassesScreen.kt` |
| Older phone screen | `tools/android-dev/PhonePracticeActivity.kt` (still registered, no launcher; Unity calls its statics) |
| Glasses side (dev scene) | `tools/android-dev/DevLessonControls.cs` (auto-start, auto-listen, phone panel), `DevReferenceBuild.cs` (scene + build) |
| Shared Unity runtime | `Assets/Scripts/Learning/*` (`LearningAgentClient`, overlay, `PortalSpeechToText`), `Assets/Scripts/Visko/*` |
| Portal | `portal/agent/*` (turns, scoring in `guardrails.js`), `portal/practice/*` (photo practice), `portal/scenarios/*` (written lessons), `portal/records/*` (photos, sessions, replays) |
| Tooling | `tools/android-dev/{stage.sh,patch-dev-apk.sh,README.md}`, `tools/controlglasses/*`, `tools/sim-learner/*`, `tools/eval/*` |

Phone ↔ glasses hand-off: `PracticePortal.select()` stores `[id, startImage, title]`. `requestLaunch()`/`requestEnd()` are one-shot flags that `DevLessonControls` polls every 0.5 s through `PhonePracticeActivity.consume*Request()`. Photo practices keep their JPEG on the phone (`pendingImage(id)`).

## Verified vs not

**Verified on the S25:**
- build → patch → install from this Mac;
- glasses detected and XR session started;
- one task in Recents;
- the dark per-eye glasses screen;
- auto-listen firing after each line (`[Flow]` logs);
- the full-screen lesson panel;
- the voice goal → lesson path;
- Vocabularies loading from the Portal.

**Not yet verified on the device:**
- the redesigned mockup panel (the phone locked before the screenshot);
- `PortalSpeechToText` (server-side speech for answers, from the glasses-flow session);
- the mute-while-listening change;
- the Tenin recovery screens;
- photo practice end to end with the new goal step;
- End lesson → summary;
- Review → replay into the glasses.

## Known issues and next steps

1. **Answer recognition**: Android's recognizer often returned "error 7" (no match) or fragments. `PortalSpeechToText` (OpenAI transcription via the Portal) is now wired in; test it first. The pass mark is 60 (`PASS_SCORE`); particle swaps are capped below it.
2. **Replays of photo lessons** start from the scene prompt, because the glasses can only load bundled `lessons/…` images.
3. **Live voice conversation (Talk)** was removed from the lesson panel to keep one control. The Cartesia agent is still configured as a convenience-store clerk.
4. **No pause.** Tenin's Pause button stays hidden (`TeninHooks.pauseLesson` unset).
5. **Spoken-goal level** is fixed at N4 (`TeninWorkflow.LEVEL`).
6. **Layout**: the in-glasses text is small. Large system font sizes are untested.
7. **Merge `feat/unified-flow` into `main`** once a full device run passes. `main` has diverged only by a docs commit, already merged here.

## Working with several agents

Three Claude sessions worked in this one checkout: integration (this doc), glasses-flow fixes (`EVAL-glasses-flow-2026-11-15.md`), and scoring/simulation (`tools/sim-learner`). Agree file ownership before editing. `stage.sh` copies whatever is in the working tree, including uncommitted work from other sessions.
