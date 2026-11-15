# Glasses flow eval — 2026-11-15

Questions: why does the app "crash after Start session", and does the lesson really run without pressing Speak?

Method: a continuous log capture on the S25 while a person ran the Tenin builds with the Air 2 Ultra, matched against the portal's turn log, plus a read of the flow code (`tools/android-dev/DevLessonControls.cs`, `LearningAgentClient.cs`, `JapaneseSpeechRecognizer.kt`) and the XREAL SDK. Times are the phone's local time (UTC+7). Evidence excerpts come from `tools/eval/flow_timeline.py` (see the end).

Builds observed: `ImmerseTheBay-tenin-s25.apk` (installed 17:11) for runs 1–2, `ImmerseTheBay-tenin-one-app-s25.apk` (installed 17:26) for run 3. The working tree has changed since (controller now leads with **End lesson**; **Start session** is **Start again**); the listening logic is unchanged.

## Verdict

- **The "crash" today was the phone cutting power to the glasses at low battery, not app code.** Power was steady for 8 minutes while the battery was 16–19%. At 17:20:09, as the battery reached 15% (Android's low-battery warning), the phone stopped powering the glasses. Below 15% power dropped 11 more times by 17:34 (17 s to 2 min apart, usually back within 2–5 s), not counting one deliberate cable swap to the Mac. Every cut makes XREAL tear down the XR view. Two older native crashes (yesterday evening's builds) happened inside XREAL's display start-up and may share the cause; unproven.
- **Listening does start without pressing Speak, but it is not hands-free in practice.** The app opens short recognizer windows (~3 s each, a beep each time), only through the glasses' microphone next to the glasses' speakers. It stops for good after 4 tries (~15 s). In the one complete run, 1 of 4 windows heard words, and the person pressed **Start session** while the app was already listening, which suggests nothing told them the lesson had started.

## 1. "It crashes after Start"

| Run | What happened | Cause |
|---|---|---|
| 1 (17:12–17:16) | XR opened immediately after "I'm wearing them", lesson started by itself, one answer reached the portal. The person left via Recents at 17:13:19, came back at 17:13:55 to a dead lesson (5 taps, no turns), and swiped the app away at 17:16:22. | No crash. Leaving the XR view cancels the lesson (`LearningAgentClient.OnApplicationPause` → `CancelWork`) and nothing resumes it. |
| 2 (17:18–17:20) | XR opened at 17:19:36.96 right after "I'm wearing them", display surface created fine, automatic start at 17:19:44.99, video library loaded at 17:19:45.3. **At 17:19:46.99 the app itself reopened the phone screen with no touch**, pausing the XR view. At 17:20:09.96 the battery hit 15%, the phone stopped powering the glasses, and XREAL quit the XR side (`[XREALPlugin] ForceKill`). | Power cut, preceded by an unexplained automatic return to the phone (see open questions). |
| 3 (17:26–17:27) | Newest build. XR opened at 17:26:48.8; at 17:27:05.2 (battery 13%) power was cut with the XR view in front and XREAL quit immediately (`QuitApplication (deinitializeLoader: True)`). | Power cut. |

Evidence chain for a cut, all within 0.4 s:
- `usb_otg STATE=SINK`: the phone stops sourcing power.
- `USB port changed: mode=none`.
- `GlassesControl … USB_DEVICE_DETACHED → forceQuitApps`.
- `[XREALPlugin] ForceKill`.
- Then `BATTERY_LOW` and the 15% warning.

XREAL's SDK always tears down the XR side when the glasses detach (`XREALCallbackHandler`: `ACTION_TYPE_DISCONNECT` / `ACTION_TYPE_FORCE_QUIT` → `QuitApplication`). In multi-resume mode only the XR part goes (`forceKill: multiResumeMode=true`), so the person lands back on the phone screen with the glasses view gone, which looks like a crash.

Older native crashes (dropbox, 10:20 and 10:46): SIGSEGV null dereference in XREAL's runtime (`libnr_api.so` from ControlGlasses) under `NRSwapchainCreateEx`, called from `XRDisplaySubsystem::GfxThread::Start`, 25–30 s after the process started. XREAL creates that swapchain on the first XR start and on every resume of the XR view (logged at 17:13:55 as `CreateSwapchainEx`). A display that vanishes or is re-created at that moment is a plausible trigger (not yet reproduced). The battery was at 1% at 16:56 during earlier tests, but its level at 10:20/10:46 is unknown.

## 2. Does it run without pressing Speak?

Run 1, the only run that reached the conversation:

| Step | Time | Gap |
|---|---|---|
| "I'm wearing them" tapped | 17:12:40.70 | |
| XR view opens | 17:12:40.73 | immediately |
| Automatic `StartLive` | 17:12:48.1 | 7.4 s |
| Opening turn on the portal | 17:12:54.6 | 6.5 s |
| First recognizer window | 17:13:02.0 | 7.4 s (≈21 s after the tap) |

| Window | Opened | Result |
|---|---|---|
| 1 | 17:13:02.0 | `NO_SPEECH_DETECTED` after 2.6 s (person pressed **Start session** at 17:13:04.7) |
| 2 | 17:13:05.8 | `NO_SPEECH_DETECTED` after 3.0 s |
| 3 | 17:13:09.9 | "ここ開いていますか" → portal score 57, asked to repeat |
| 4 | 17:13:18.4 | `NO_SPEECH_DETECTED` (person left the app during it) |

Problems, each with evidence:

1. **The microphone hears the glasses.** The recognizer used the glasses' USB microphone (`getInputForAttr found device type is 0x82000000`) while the video's ambient audio played through the glasses' speakers (`AUDIO_DEVICE_OUT_USB_HEADSET`). Google's start tone played on the glasses at 17:13:02.03–02.62 and the recognizer reported "start of speech" at 02.41. Every window opens already "hearing speech" (0.4 s after the mic opens) and then ends with nothing.
2. **Short windows, then it stops.** About 3 s per window when no words follow, a 1 s deaf gap, a beep each time, and `MaxSilentRetries = 3` → 4 windows (~15 s) per line. After that only **Speak** restarts it. Between windows the glasses show "Speech input: No speech was recognized".
3. **Little tells the learner it is listening.** Apart from the beep, the only cues are a "Listening…" status line in the glasses and the in-glasses Speak button relabelled "Finish". The person pressed **Start session** 1.9 s into window 1 (stack: `DevLessonControls:StartLive` ← `UnityEvent:Invoke` ← `XRUIInputModule:ProcessTouches`).
4. **Speak on the phone stops an automatic listen.** The phone controller's **Speak** label never changes, and `LearningAgentClient.Speak()` calls `StopListening` when a recognition is pending.
5. **Nobody speaks first when the learner opens.** This photo lesson's first line is the learner's own greeting ("こんにちは。ここ、空いていますか。"), so the partner had nothing to say and the first sound the learner heard was the beep. (The portal had been restarted at 02:08 and already includes the partner-line change.)
6. **Correct speech scored as wrong.** The person said "koko, aite imasu ka" but the recognizer wrote 開いて ("open") instead of 空いて ("free"). Text-only similarity gave 57/100 and asked them to repeat. Compare readings (kana) as well.
7. **Leaving the XR view kills the lesson silently.** Video frames stopped at 17:13:22 and only 2 arrived after the resume. `ViskoSession` has no pause handling, so the video session is not closed while paused (possible idle billing; unverified).

## 3. Phone screens (Tenin)

- `TeninActivity` is destroyed and recreated on rotation (17:11:58, `changed={CONFIG_ORIENTATION}`) and a new copy opens when the glasses connect (17:12:15). Screen state is lost.
- `TeninWorkflow.ui()` drops results when the activity that started the work is destroyed, so a lesson finishing during a rotation is created on the portal and never shown.
- Fix: lock to portrait or declare `configChanges` for `TeninActivity` in `PhonePractice.androidlib/AndroidManifest.xml`, and deliver workflow results to the current activity.

## 4. Recommended changes, in order

1. **Testing practice (no code):** keep the phone above ~30% for every glasses test, or power it through a USB-C adapter that passes power through while driving the glasses. Re-test before chasing any other "crash".
2. **Phone side handles glasses loss:**
   - warn on **Start in glasses** when the battery is ≤ 20% or the glasses display is missing;
   - after a disconnect, show "Glasses disconnected — reconnect and tap Continue" and resume the lesson.
3. **Find the automatic return to the phone screen in run 2** (17:19:46.99, `ShowPhone`-style intent with `FLAG_ACTIVITY_REORDER_TO_FRONT`, no touch) and replace it with an error shown in the glasses plus a retry.
4. **Real hands-free listening:**
   - Keep the glasses speakers out of the microphone: duck the video audio while listening, use the phone microphone, or use the echo-cancelled live voice path ("Talk").
   - Listen continuously until the learner speaks instead of 4 × 3 s windows, with no beeps.
   - Keep a visible "Your turn — say: …" prompt up while listening.
5. **Conversation start:** when the learner opens, show and play a clear "your turn" cue (or have the partner greet first).
6. **Controller:**
   - show the listening state on the phone;
   - make **Speak** during an automatic listen do nothing harmful;
   - keep **Start again** out of the main row (already done in the working tree).
7. **Score by reading** as well as text.
8. **Pause/resume:** resume or offer restart after returning to the XR view, and close the video session on pause.
9. **Instrument the flow** with `[Flow]` log lines (auto-start, listen start/end and reason, gave up, video session failed, returning to phone), so this eval no longer has to infer them from stack traces.

## 5. Repeat the eval

```sh
tools/eval/capture-device-log.sh /tmp/glasses-run.log          # start before the test, Ctrl-C after
python3 tools/eval/flow_timeline.py /tmp/glasses-run.log \
  --portal portal/agent/logs/$(date -u +%F).jsonl --from 17:12:00 --to 17:20:00
```

The scorecard reports:
- recognizer windows and how many heard words;
- spoken answers that reached the portal;
- controller taps that ran a handler, and XR pauses;
- glasses power cuts and USB disconnects, XREAL quits, native crashes;
- the battery range, and why each process exited.

By default only video-session turns are counted, so practice-mode and `tools/sim-learner` traffic (which hit the same portal from 17:33) is excluded.

Suggested protocol, phone charged:
- 3 preset lessons and 2 photo lessons, speaking every line;
- one run where the person leaves to Home and comes back;
- one run with Start pressed within 10 s of plugging in the glasses.

## Fix status (03:10 Mac time)

| # | Item | Status | Owner |
|---|---|---|---|
| 1 | Charge the phone for tests | Testing practice | user |
| 2 | Phone side handles glasses loss | Done in the Tenin screens, not yet run on the device:<br>- battery warning at ≤ 20%;<br>- **I'm wearing them** waits until the glasses are connected;<br>- "Your glasses disconnected" / "Lesson stopped" screen with **Start again** when the person returns without **End lesson**;<br>- portrait lock, and `configChanges` in the manifest (`1bff1e3`). | immersethebay-18, -86 (manifest) |
| 3 | Automatic return to the phone | Removed: it was `DevLessonControls` bringing Tenin to the front after the automatic start | immersethebay-86 |
| 4 | Hands-free listening | Done, not yet run on the device:<br>- `PortalSpeechToText` is the dev scene's speech component: it waits up to 20 s for speech, ends after a 0.9 s pause, transcribes on the portal (Japanese hint), and plays no tones;<br>- the scene audio is muted while either speech component listens. | immersethebay-18 |
| 5 | "Your turn" cue when the learner opens | Open | — |
| 6 | Listening state on the phone controller | In progress (large Speak button showing "Listening…") | immersethebay-86 |
| 7 | Reading-based scoring | Not started; owner is raising it with the user | immersethebay-3b (scoring) |
| 8 | Pause/resume | The video session now ends when the XR view pauses (no idle billing). Returning to Tenin shows "Lesson stopped" with **Start again**; resuming mid-lesson is not supported. | immersethebay-18 |
| 9 | `[Flow]` log lines | In progress | immersethebay-86 |

## Open questions

- ~~What made the `tenin-s25` build return to the phone screen 2 s after starting the video session (run 2)?~~ Answered: `DevLessonControls` brought Tenin to the front after the automatic start; removed by the integrator.
- Does the native `NRSwapchainCreateEx` crash reproduce at full battery?
- After an XREAL force-quit, does **Start in glasses** work again in the same process?
- Does the video session keep billing while the XR view is paused?
