# Tenin phone UI — handoff for integration

Date: 2026-11-15. Branch: `feat/unified-flow` (working tree shared with the phone-workflow work). Nothing here is committed yet.

The phone screens from `Tenin_phone-hifi/` are built as native Android views in new files only. They are not yet part of the app: `stage.sh`, the manifest, `PhonePracticeActivity.kt` and `DevLessonControls.cs` were left untouched because the workflow integration is editing them. This document lists what exists and the exact steps to wire it in.

## What exists

| Path | Purpose |
|---|---|
| `tools/android-dev/tenin/TeninUi.kt` | Colours, type, icons (drawn in code, no drawable resources), cards, buttons, header, tab bar, page scaffold. |
| `tools/android-dev/tenin/TeninScreens.kt` | One stateless function per screen: data and callbacks in, `View` out. Data classes `TeninPhrase`, `TeninScenario`, `TeninSession`, `TeninStep`. |
| `tools/android-dev/tenin/TeninActivity.kt` | Navigation, pre-made lessons from bundled `lessons/*.json`, Review and Vocabularies from the portal, text-to-speech, and `TeninHooks` (the seams for the workflow). Also `TeninDemo` sample content. |
| `tools/android-dev/tenin/assets/fonts/` | EB Garamond regular and italic variable fonts plus `OFL.txt` (SIL Open Font License). Loaded with `Typeface.Builder(assets, …)`; falls back to the platform serif if missing. |
| `tools/android-dev/tenin/preview.sh` | Builds the screens alone into `Builds/tenin-preview.apk` in about 20 s using Unity's bundled Kotlin compiler and Android build tools. No Unity or Gradle run. |
| `tools/android-dev/tenin/preview/TeninSnapshot.kt` | Preview build only. Draws every screen to PNG off-screen, so layouts can be checked while the phone is running the XR app. Do not stage this file into the app. |

No `R` class, XML layout or new dependency is used. Package is `com.immersethebay.phone`, same as `PhonePracticeActivity`.

## Screens and status

Checked by rendering on the S25 (1080×2340) through `TeninSnapshot` and comparing with the mockups. Interaction (taps, scrolling, keyboard, speech) has not been exercised on a device, because the phone was in use by the XR app the whole time.

| Mockup | Function in `TeninScreens` | Working without the workflow? |
|---|---|---|
| 1 · Start | `start` | Yes |
| 2 · Choose a scenario | `chooseScenario` | Yes, lists the bundled lessons |
| 3 · Scenario detail | `scenarioDetail` | Yes, first three lines of the lesson, speaker buttons use on-device Japanese text-to-speech |
| 2 · Live photo or voice | `liveLesson` | Layout only; buttons call `TeninHooks.liveLesson` |
| 3 · Say my goal listening | `sayGoal` | Layout only; driven by `TeninActivity.showSayGoal` |
| 4 · Reading your photo, 5 · Photo ready | `photoProgress` | Layout only; driven by `showPhotoProgress` (also has an error state with Try again) |
| 4 · Here's your lesson | `lessonSummary` | Layout only; driven by `showLessonSummary` |
| 6 · Put on your glasses | `putOnGlasses` | Yes |
| 7 · Lesson running | `running` | Yes; step progress and Pause appear only when their hooks are set |
| 8 · Lesson complete | `complete` | Shown after End lesson when the portal has a session from today with the same title |
| Review, Session detail | `review`, `sessionDetail` | Yes, from `GET /api/sessions` and `/api/sessions/:id` |
| Vocabularies, Phrase detail | `vocabularies`, `phraseDetail` | Yes, built from phrases in saved sessions |
| 3 · Camera | not built | The app uses the system camera through an intent. An in-app viewfinder needs Camera2 code or CameraX (a new dependency). |

## Steps to integrate

1. **Stage the sources.** In `tools/android-dev/stage.sh`, copy `tools/android-dev/tenin/{TeninUi,TeninScreens,TeninActivity}.kt` to `$dev/Assets/Plugins/Android/` and `tools/android-dev/tenin/assets/fonts/` to `$dev/Assets/Plugins/Android/PhonePractice.androidlib/assets/fonts/`. The three `.kt` files have no `.meta` files yet; Unity will generate them on import and they should be committed like `PhonePracticeActivity.kt.meta`.
2. **Register the activity.** In `PhonePractice.androidlib/AndroidManifest.xml` add `com.immersethebay.phone.TeninActivity` with the same theme and `windowSoftInputMode` as `PhonePracticeActivity`. To make Tenin the phone home screen, move the `MAIN`/`LAUNCHER` filter to it; keep exactly one launcher entry. Text-to-speech on Android 11+ also needs `<queries><intent><action android:name="android.intent.action.TTS_SERVICE" /></intent></queries>`.
3. **Let Unity read the selection.** `TeninActivity.selectedSession()` returns `[id, startImage, title]`, the same shape as `PhonePracticeActivity.selectedSession()`. Either point `DevLessonControls.StartLive` and `OpenPhonePractice` at `TeninActivity`, or have `PhonePracticeActivity.selectedSession()` return `TeninHooks.selected` when it is non-empty. Photo lessons still need `pendingImage(id)` from wherever the workflow keeps the JPEG.
4. **Wire the Live Lesson workflow** through `TeninHooks` (below).
5. **Server.** Review, Vocabularies and replay need the portal from `feat/unified-flow` (`/api/sessions`, `/api/sessions/:id`, `/api/sessions/:id/replay`). On a portal without those routes the two tabs show "Could not load your sessions".

## TeninHooks

All hooks are optional static fields set once, for example in `Application`/activity start-up.

| Hook | Called when | Expected behaviour |
|---|---|---|
| `liveLesson: (TeninActivity, kind) -> Unit` | Take a photo (`"photo"`), Choose from library (`"library"`), Say my goal (`"voice"`) | Run capture, recording and generation, updating the UI with the `show…` methods |
| `openGlasses: (Activity) -> Boolean` | "I'm wearing them" | Open the XR view for `TeninHooks.selected`; return false to stay on the screen. Default starts `ai.nreal.activitylife.NRXRActivity` |
| `endLesson: (Activity) -> Unit` | End lesson | Stop the lesson in the glasses |
| `pauseLesson: (Activity) -> Unit` | Pause | Pause or resume. The button is hidden while this is null; Unity has no pause today |
| `progress: () -> Pair<Int, Int>?` | Running screen is drawn | Current step and step count, or null to hide the bar |

Methods on `TeninActivity` for the workflow to call on the main thread:

```kotlin
showSayGoal(transcript, listening, level, onMic, onClose, update = false)   // update = true redraws in place
showPhotoProgress(photo, steps, ready, error = "", onContinue, onRetake, onCancel, update = false)
showLessonSummary(icon, situation, remember, practice, onAddPhoto, onContinue, onAgain)
showPutOnGlasses(lessonId, startImage, title)   // sets TeninHooks.selected, then the glasses screen
showComplete(summary, saved, review, tip)
```

Typical photo flow: `liveLesson("photo")` → system camera → `showPhotoProgress(bitmap, [TeninStep("Uploading your photo", true), TeninStep("Building your lesson…", false)], ready = false, …)` → on success the same call with `ready = true, update = true` and a final step such as "Lesson ready: <title>" → `onContinue = { showPutOnGlasses(lesson.id, "", lesson.title) }`. On failure pass `error = "<message>"`.

## Where the build differs from the mockups, on purpose

- **Privacy card on Live Lesson.** The mockup says the photo "stays private" and is "deleted when the lesson ends". Today the photo is sent to the lesson service, OpenAI and the video provider, and the portal keeps it up to 30 minutes. The card says "Your photo is used for this lesson. It is sent to build the lesson and removed from the server within 30 minutes." Change the wording only if the behaviour changes.
- **"Put on your glasses".** "It starts when you look up" and "Mic on in the glasses" are not true yet (the wearer taps "I'm wearing them", and the mic opens when the lesson starts). Text is "Tap the button below once they're on" and "The glasses listen once the lesson starts".
- **Reading-your-photo steps.** "Spotted a restaurant" and "Found the menu board" need the generator to report what it saw; `/api/practice/generate` returns only the finished lesson. The screen takes any list of steps, so use real stages until that data exists.
- **Choose a scenario.** Tapping an unselected card selects it; tapping the selected card opens Scenario detail; "Start in glasses" starts the selected lesson. A one-line hint under the list says so. The mockup does not show how detail is reached.
- **Session headline and tip, "Word by word", "Swap the dish".** These need written feedback and a word breakdown per phrase, which the server does not produce. The sections are hidden when the data is empty and appear in the demo content.
- **Pre-made lessons.** The list shows the two bundled lessons (convenience store, home), not the four in the mockup.
- **Scaling.** All sizes scale by screen width ÷ 390 (0.92 on the S25) so the mockup proportions hold.
- **Shadows** are standard view elevation tinted purple; they are not drawn in the off-screen snapshots.

## Preview and review

```sh
tools/android-dev/tenin/preview.sh                       # builds Builds/tenin-preview.apk
adb install --no-incremental -r Builds/tenin-preview.apk # separate package: com.immersethebay.tenin.preview

# On screen, with the mockup sample content. Optional: --es screen <name>
adb shell am start -n com.immersethebay.tenin.preview/com.immersethebay.phone.TeninActivity --ez demo true

# Off screen (does not interrupt the foreground app): writes one PNG per screen
adb shell am broadcast --include-stopped-packages -n com.immersethebay.tenin.preview/com.immersethebay.phone.TeninSnapshot
adb exec-out run-as com.immersethebay.tenin.preview cat files/tenin/start.png > start.png
```

Screen names: `start choose detail live goal summary reading ready glasses running complete review session vocabularies phrase`. Without `demo`, the activity uses the bundled lessons and the portal in `visko.json`.

`adb` here is Unity's copy: `/Applications/Unity/Hub/Editor/6000.0.85f2/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb`. The preview package is still installed on the S25; remove it with `adb uninstall com.immersethebay.tenin.preview`.

## Not verified

- Any tap, scroll, back navigation, search typing or text-to-speech on a device.
- The portal-backed paths (Review, Vocabularies, replay, Lesson complete) against a running portal.
- The indeterminate spinner on "Reading your photo" (it does not draw off-screen).
- Compilation inside the Unity Gradle build. The standalone build uses Kotlin 2.2.21, JVM target 17 and `android-35`; the Unity project's Kotlin version may be older.
- Behaviour with large system font sizes.

## Also on this branch (already committed)

- `63e3716` merges the photo library, saved sessions, replay and scenario generator into the portal (web page files still `main`'s).
- `a6eafdf` makes `/api/practice/generate` return the partner's role and the partner's line before each learner reply, and passes both to the live voice session. Portal tests: 193 passing with mocked providers.

## Integration status (2026-11-15)

Steps 1–4 are done; the app built with zero errors (Unity 6000.0.85f2, project Kotlin 1.7.22) and is installed on the S25.

- **Staging and manifest.** `stage.sh` copies the Tenin sources, `PracticePortal.kt`, `TeninWorkflow.kt` and the fonts (into `PhonePractice.androidlib/assets/fonts/`, confirmed in the APK). `TeninActivity` is the only launcher entry; `PhonePracticeActivity` stays registered without one. The TTS `<queries>` entry is added.
- **Shared workflow.** `tools/android-dev/PracticePortal.kt` holds photo encoding, Portal calls (`/api/agent/transcribe`, `/api/practice/generate`, `/api/scenarios`) and the glasses hand-off. Both phone screens write the choice to `TeninHooks.selected`; Unity keeps calling `PhonePracticeActivity.selectedSession()/pendingImage()`, which delegate to it.
- **TeninHooks** (`tools/android-dev/TeninWorkflow.kt`, installed from `TeninActivity.onCreate` unless `demo`):
  - Take a photo / Choose from library → photo progress → photo practice (`/api/practice/generate`; a photo alone uses a generic conversation goal) → Photo ready → Put on your glasses.
  - Say my goal → recording with live level → server transcription (language auto-detected) → written lesson (`/api/scenarios`, N4) → Here's your lesson. **Add a photo** reuses the spoken goal for a photo practice.
  - "I'm wearing them" sets a launch request; the glasses start the selected lesson without Start session. End lesson sets an end request the glasses honour.
  - `progress` and `pauseLesson` remain unset (bar and Pause hidden).
- **TeninActivity edits:** install the workflow, forward `onActivityResult`/`onRequestPermissionsResult`, cancel recording in `onDestroy`.
- **Unity (`DevLessonControls`):** Choose session opens Tenin; Portal-stored lessons (`gen-`, `rec-`) may start without an image; only bundled `lessons/…` images are loaded, other start images fall back to the prompt; after each finished step the glasses listen automatically (up to 3 silent retries).
- `preview.sh` also compiles `TeninWorkflow.kt` and `PracticePortal.kt`.

Verified on the S25: Tenin opens as the home screen with the EB Garamond fonts; Vocabularies loaded saved phrases from the Portal. Not yet exercised: camera/library and spoken-goal flows, Put on your glasses → auto-start in the glasses, End lesson, Review and replay into the glasses.
