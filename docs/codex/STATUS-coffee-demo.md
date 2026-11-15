# Coffee demo release checklist

November 15, 2026. Android source `a2835a1`. Portal runtime `2da42f1` at port 8793. Quest source `2ffa98a` on its independent `main`. The original XREAL SDK 3.1 MR reference is retained.

Unity Android built with zero errors. The signed `ImmerseTheBay-s25-a2835a1.apk` was installed and matched device SHA-256 `e81c9b38dabe2f4705570f613ad244e9e3e74575baf20d94d9fa614f46454d24`. Tenin activity started. The S25 reported the lock screen and only its built-in physical display during this checkpoint. The Portal suite passed 244 tests and all 12 final MP4s decoded.

| Priority | Area | Current evidence | Remaining work and acceptance | Tracking |
|---|---|---|---|---|
| P0 | Phone Courses | Compiled. Order Coffee defaults to first and selected. Courses replaces Pre-made Lesson. English and Japanese use separate rows. An Afternoon at Home is hidden from the new-course catalog. | Confirm all changes in the integrated S25 app and that older saved Home runs still replay. Check course list and detail previews. | #21, #22 |
| P0 | Portrait capture | Standalone Android build and 640 geometry cases passed. The camera shows a 16:9 preview and encodes a frame from that view. Gallery uses the existing EXIF and crop path. | See the same four landmarks at the edges before and after capture on S25, with upright orientation. Verify Back, rotate, retake, background/resume, camera permission and upload. | #35 |
| P0 | On-device café | Café order, milk and handoff each have their own start image and reviewed Dynamic movement. The earlier store wearer run verified three sentences and retry. | Run all three café scenes on S25/Air 2 Ultra. Check both eyes, fixed first-person viewpoint, identity, feather, clerk/guide speech, transcript cues, score and summary. | #1, #5, #16, #37, #41 |
| P0 | Opening cache | 12 real 6-second, silent H.264 960×540, 24 fps openings are under stable Portal data. All decode. Native plays the committed opening and downloads one predicted successor while the lesson runs. | Verify correct visual motion before first live frame, preload hits, deadline and stale/corrupt/404 fallbacks. Compare time to visible motion and time to live in 10 cold/warm transitions. A changed goal must abandon a stale prediction. | #27 |
| P0 | Gesture Explore | Real Luna located the milk pitcher. Real GPT Image generated 16:9 reseed and a live planner returned to the exact lesson target. Frame fallback took 2.2 s and image editing took 15.7 s in a sampled run. XREAL Explore and hand meshes are built. | Prove exact pointer selection on the native running frame, hand-model-only display, no hit on blank counter, audible interaction and same-sentence return. Test occlusion and double pinches. Coffee first. A convenience-store detour may still pick its predefined view rather than the same authored scene; track canonical branch media identity. | #27, #40, #41 |
| P0 | Disconnect and binocular display | Disconnect invalidation, mirror best-effort sync and stale-frame gate are implemented. Earlier independent left/right rendering worked. | Wi-Fi loss, 429, Portal restart, command timeout, USB reconnect, warm resume, DeX and split-screen regression. Budget total Kotlin command wait and verify Retry without stale work. A single-provider reseed may need Retry after the provider has accepted reset. | #28, #29 |
| P0 | Native demo recording | Shoot runbook and export tools exist. ADB can capture explicit glasses pixels and one-eye MP4. The available screenrecord path is silent. | Record actual café live action and native glasses display including fail, recover, pass and summary. Capture clean, synchronized audio. Compare both ears and export the final demo without explanatory text in the corners. | #34, #39, #43 |
| P1 | Audio and interruption | Cartesia clerk and checked stereo output; ambient ducking; voice example defaults on; two actor voices. Earlier wearer heard old audio in only one ear. | Confirm LR audio and actual no speech/music in generated ambience, volume balance, TTS toggle persistence, and learner interruption with reply recovery on native. Verify both managed voice styles and voice/browser parity when configured. | #2, #6, #8, #18, #36, #38, #39 |
| P1 | Scores and review | Real wearer saw both passing and retry cards on the store course. Immediate sound and result state, completion overlay and saved review present. | On café, sync phone and eye progress and scores. Verify result stays visible through transition, localized correction, assisted/manual paths, completion cue, exact saved run and replay after Portal restart. Current numeric metric is transcript match; acoustic pronunciation scoring remains separate. | #25, #26, #37 |
| P1 | Readability, input and vocabulary | Larger Japanese, aligned kana/romaji, English lines, highlighted target vocabulary, two review tabs, raised labeled actions, hand focus hints. | Check two-eye readability and rotation size, lower hint and waveform, laser contrast, button reach and feather against room conditions. Verify or finish phoneme/word playback timing and moving highlight for full sentence and individual words in lesson and Review. | #19, #20, #23, #38, #40 |
| P1 | Real-world lessons | Live dictation and editable goal. Photo + prompt + goal generate three distinct motion cues and N5 exchanges. Three modeled exchanges returned on provider smoke. | Run camera/voice end to end on S25. Add multiple separate generated future start images for major transitions. Verify continuity and female/male casting from photo or voice. | #17, #24, #35, #36 |
| P1 | Native synthetic learner | Synthetic browser audio showed actual recognition and scored variation. Native recording guide is ready. | Add safe native test injection or a controlled acoustic setup that can demonstrate hesitant, mispronounced and correct learner attempts in real S25/Ultra footage. Validate audio sources and the actual score path. | #43 |
| P1 | Quest World Labs | Real private wide café Draft and 100k SPZ rendered with all three 960×540 café opening videos on desktop. Quest repo synced to Portal Courses and clips. | Import the other three user-generated worlds and bind to the correct course. Check empty environment, head height, ground scale, and plate position. Verify WebXR on physical Quest 3, GPU frame time, actual shared Dynamic join without ending host, and then standalone OpenXR APK. | #32 |
| P2 | MR research | Research and prototype plans exist. | Prototype per-sentence thumbnails, tabletop and comfortable 3D actions, temporally stable depth and scene occlusion. Keep them behind tests/feature gates until they show a clear wearer benefit. | #30, #31, #33 |
| P0 release | Merge and handoff | XREAL build on integration draft PR #42 plus upstream PR #15. Quest pushed separately to main. | Complete consolidated acceptance, attach device/clip/Portal evidence, review and merge XREAL changes, update issue checklists and verify setup from a teammate's machine. | #16 |

See [delivery plan](PLAN-coffee-demo-delivery.md), [lead-in and gesture media design](IMPLEMENT-demo-video-lookahead.md), and [actual café capture runbook](RUNBOOK-coffee-demo-capture.md).

## Café menu and restart build

The S25 has the signed `ImmerseTheBay-s25-railway-57ec4a6.apk` for source revision `57ec4a6b4e11493b3d131d768cc12f2312a030e7`. The APK on the phone and the signed local package both returned SHA-256 `6daa7f9ff0feb0b75345667ba47e06419f0479bad7d992ae259a0d694736c612`. Unity built successfully. The [Railway Portal deployment](https://railway.com/project/b129c2b2-a712-4be4-8179-e2e0e14f345d/service/ea7c8910-eab2-47bd-868d-0ec4a78925b6?id=a3b2ea16-b301-4018-9982-c150c78fba70) serves the same revision. Portal syntax checks and 253 automated tests passed.

The second scene has a large centered menu beside the barista. A validated selection of **COFFEE** or **LATTE** prompts a clerk confirmation, then the learner says `これにします`. The phone and glasses share the result timing. Positive feedback uses a translucent background and lasts 1.4 seconds, including its fade. Both displays offer **Restart** to start a fresh run of the selected lesson.

Authorized byte-range requests returned HTTP 206 for the menu, coffee serving and milk-pour clips. A captured frame from the actual Dynamic menu video was sent to live OpenAI vision twice. A COFFEE hold selected the plain-coffee serving path, and a LATTE pinch selected the milk-pour path. Their live Cartesia clerk confirmations returned two-channel WAV. Those calls verify service behavior with real media. The S25 install was verified with the phone locked and the glasses disconnected. The wearer checks for both selections, native prefetch hits, optical and audible feedback, and each Restart control are pending.

## Recorded café acceptance

The S25 ran signed APK `8b09c3b` (SHA-256 `be0009349720e49b3cc90caf4284ae14a6bae5c90001e5041a4770969a0b1392`) against Portal `b09ae2a` on port 8793. The official XREAL 3.1 reference remains available as a separate baseline.

- The three authored scenes rendered on the Air 2 Ultra, and the milk and handoff openings were prefetched. The first cached frames appeared in 0.55 s, 0.15 s and 0.15 s respectively.
- The wearer pointed at the menu during the running milk scene. The left and right display captures show the official XREAL hand mesh and the aim marker. After the selected-frame request, the menu detour rendered and a successful answer automatically restored the milk sentence. Native log timestamps put point selection to visible detour at 22.94 s.
- The S25 microphone heard slowed male synthetic learner speech played from the Mac. The app produced one retry for coffee and one for milk. Coffee, the menu detour, milk and thanks each then matched at 100 percent. The phone and glasses automatically displayed a six-attempt review. Clearing the lesson returned to Tenin and released the Dynamic session.
- Guide pronunciation uses Cartesia Yuzuki, set through `CARTESIA_VOICE_ID_TUTOR`, and authored character dialogue uses its own configured cast. Native logs measured about 2.0 s between scene dialogue finishing and guide pronunciation starting. The phone Settings switch defaults to follow-along and writes the setting used by the Unity lesson. Timed highlighting consumes the WAV sample clock in the glasses.
- The local 1080p demo is `Builds/tutor-demo-20261115.h0CO9p/final/coffee-demo-s25-air2-ultra.mp4`. Phone framing is portrait on the left and a real glasses eye is on the right. Scrcpy recorded Android playback in stereo. The same slow male fixtures used at the microphone were added to the edit at their playback times. There is no web video or simulated hand footage in the export.

The photo dialog now follows phone rotation while capture is open, uses a proportionally cropped 16:9 preview, and restores portrait on close. A physical landscape/portrait comparison, a repeatable optical button-selection test and device-level saved Review highlight verification remain open.

## Judge rehearsal build

The S25 has signed APK `2962b12` (SHA-256 `7a75f3e80db888427121b25b9b0f7352cef69b01db91811643f0e72f47a602a6`) with the Portal behavior from `d54f6e7` at port 8793. Unity Android built with zero errors. The Portal suite passed 251 tests, and all three café opening endpoints returned valid partial content during preflight.

The pre-course Android Presentation originally drew the Tenin title at 22 sp, about 9 visible pixels on the 1920×1080 image per eye. Unity's Order Coffee title appears after entering the class. The new build sets the standby text size before the external display's first layout and uses the physical per-eye dimensions. S25 capture at the home and ready screens measured a 70-pixel Tenin title and 49-pixel guidance in both eyes.

The current Portal process loaded the recorded café run from disk. The full replay retained four practiced sentences and six attempts in the original record. The new tricky replay contains only the coffee and milk requests, which each needed a retry. Timed phone examples are included in the build.

## First café wearer pass

S25 ran the signed `a2835a1` APK against Portal `2da42f1`. The handset showed Courses, separate English and Japanese title rows, and Order Coffee selected. The wearer confirmed the milk scene was visible and sound reached both ears.

| Scene | Cache selection | First cached frame | Dynamic handoff |
|---|---|---|---|
| Order | Cold | 0.57 s | 13.66 s |
| Milk | Prefetched | 0.14 s | 9.08 s |
| Handoff | Prefetched | 0.15 s | 9.40 s |

These are one-run native logs, not percentile measurements. Physical attempts passed at 100 and 90 percent with several visible milk retries. The final line was skipped, which opened the original summary. This run did not confirm a gesture detour. ADB produced a decoded 100-second 2560×720 SBS clip and 1280×720 single-eye export without audio. The recording begins during the milk scene and includes retry, pass and the handoff transition. Local media and timing logs are kept in `Builds/coffee-hardware-20261115-071544/`.

## Wearer feedback integration

Source `1b2ece0` adds larger clerk subtitles, lower action placement, rightmost Speak/Finish, phone lesson actions, and a visible unlit hand mesh. Passing a sentence preserves the old scene for at least one second and transitions with eased fades. The scripted café clerk uses a separate, revision-bound reply before the next scene or retry, with the proactive prompt and learner example before the microphone.

On completion, both surfaces present a per-attempt review. The learner can read the recognized text and match score and play the spoken recording or the target example. Recordings remain in memory on the S25 for the current review only (at most 12). Saved Review continues to store existing session data; microphone audio is not added to the Portal record. The combined native timing, hand visibility, and audio playback still require wearer acceptance.

The Unity build of `1b2ece0` passed with zero errors. Its signed APK was installed by wireless ADB and returned SHA-256 `7233522b96d98df60b51d6b6a63383a88c488f317363ab921508c4e57029eabe`. Portal 8793 is on the matching revision and passed 247 tests. Real Cartesia requests produced stereo PCM for the café welcome, retry and closing. After restarting Portal, the exact earlier S25 run was retrieved and compiled into a three-target replay. The phone display was off and the external display was absent while applying this update. Further optical and replay audio checks need the connected wearer.

## Native command recovery checkpoint

Android source `0817212` passed Unity compilation and Android packaging. The signed APK installed on the S25 and returned SHA-256 `eb7a8e3a81f119a783d5394225360d48b85bbc79992f71d77868002a6e52f3d5`. Portal remains at `1b2ece0`. The new budget and late-frame handling passed the isolated command harness described in [native operation deadlines](IMPLEMENT-demo-unity-startup.md#native-operation-deadlines). Real disconnect/Retry acceptance awaits a connected headset.
