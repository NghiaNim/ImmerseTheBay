# Record the Order Coffee demo

Use the native Tenin app on the S25 with Air 2 Ultra attached and the development Portal reachable. Keep Voice example enabled. On the phone, open **Courses**, leave **Order Coffee** selected, and tap **Start in glasses**. Pause at **I’m wearing them** while the recordings start.

## Rehearse the judge flow

1. Check that the standby screen appears in both eyes and the phone says **Scene ready: Order Coffee**. Keep the phone unlocked and verify that the APK's configured Portal can serve all three scene openings.
2. Enter the lesson. Confirm the café appears upright, the clerk caption includes English, and the teaching voice follows the clerk with approximately two seconds between them. Wait for **Listening** before answering.
3. Order coffee. On the milk step, try `こんばんは` once and check the retry card in the phone and the glasses. Point at the dark menu beside the barista and hold the pose until the aim confirmation appears. Answer the generated detour, return to the milk request, and finish with the handoff sentence.
4. Check the attempt review on both displays. On the phone, open the saved session and try **Practice the tricky ones**. A sentence that needed a retry belongs in this replay even after the learner passes it. Stop the lesson before rehearsing USB or network recovery separately.

## Deploy the judge Portal to Railway

Use project **tenin-demo**, environment **production**, service **portal**. Keep one replica in US West with sleep disabled. The HTTP health check is `/api/health`. The volume mounts at `/app/portal/data` and stores clips and records. Provision `PORT=8790`, a private `PORTAL_TOKEN`, and the required Reactor, OpenAI and Cartesia settings from `portal/.env.example` as Railway service variables.

Prepare an upload from the committed revision and the local authored opening cache. From a directory with Railway CLI access, run:

```sh
stage="$(mktemp -d)"
sh portal/deploy/stage.sh /path/to/portal/data/scene-openings "$stage"
(
  cd "$stage"
  railway up --detach --project b129c2b2-a712-4be4-8179-e2e0e14f345d \
    --environment production --service portal
)
```

The staged archive contains source and matching authored course clips. The entry point seeds missing clips on the mounted volume. Verify that authorized byte-range requests for `cafe-order`, `cafe-menupour`, and `cafe-serve` return HTTP 206. Configure `portalUrl` and `portalToken` in the ignored development build's `visko.json` before building the hosted APK. Keep the token in the private demo build and server settings.

## Shoot the lesson

| Beat | What to capture in the glasses | Learner line |
|---|---|---|
| Order | Barista lifts the order pad, then the app shows success | コーヒーをください。 · Kōhī o kudasai. · A coffee, please. |
| Add milk | Milk pours into the cup, then the app shows a retry and recovery | ミルクをお願いします。 · Miruku o onegaishimasu. · Milk, please. |
| Receive it | Barista hands over the cup, then the lesson summary appears | ありがとうございます。 · Arigatō gozaimasu. · Thank you. |

1. Start the café camera, separate audio, and the glasses capture. Say the take number aloud. With the phone and its screen in the café camera frame, tap **I’m wearing them**. Leave a short pause before the first line.
2. Look straight ahead for the barista and both captions. Wait for each clerk line and voice example, then speak when the listening indicator appears.
3. On the milk step, say **こんばんは** (*Konbanwa*, “Good evening”) once during **Speak**. Hold the shot for **Please try again** and the score, then say the milk request on the next listening cue. Continue through the cup handoff and hold the summary for two seconds.
4. For live-action coverage, film an establishing shot, a medium shot of the learner wearing the glasses and holding the phone, and close-ups of coffee being prepared and handed over. Keep the actual wearer take and the matching glasses feed together. Use the in-app captions and score cards with a clean frame free of added corner text.

## Capture the Air 2 Ultra display

Run from the repo root on the Mac with Python 3, `ffmpeg` with `libx264`, and `ffprobe`. Use the S25’s current wireless debugging connection address so the glasses can occupy USB-C. Create a new take directory for every recording.

```sh
export ADB="/Applications/Unity/Hub/Editor/6000.0.85f2/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb"
export ADB_SERIAL="paste-s25-wireless-ip:port"
"$ADB" connect "$ADB_SERIAL"
python3 tools/android-dev/capture-demo.py displays

# Copy the physical ID labeled Air 2 Ultra in the display listing.
AIR2_DISPLAY="paste-air-2-ultra-id"
TAKE_DIR="$HOME/Downloads/tenin-coffee-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$TAKE_DIR"
python3 tools/android-dev/capture-demo.py record "$AIR2_DISPLAY" "$TAKE_DIR/glasses-raw.mp4" --view glasses --seconds 120
python3 tools/android-dev/capture-demo.py export-eye "$TAKE_DIR/glasses-raw.mp4" "$TAKE_DIR/glasses-left.mp4" --eye left
python3 tools/android-dev/capture-demo.py inspect --decode "$TAKE_DIR/glasses-raw.mp4" "$TAKE_DIR/glasses-left.mp4"
```

The raw file is 2560×720 side by side. The editor copy is 1280×720 from one eye. Check both halves of the raw frame for upright video and captions before choosing the eye. For a phone insert, repeat the lesson and record the physical phone ID with `--view phone` to a separate file. [Capture troubleshooting and SDK FPV options](../../tools/android-dev/README.md#record-the-glasses-and-phone-for-the-demo)

## Align and check the edit

ADB capture is silent and contains only the rendered display. Record the learner and room on the café camera or a separate recorder. Use a verified SDK FPV take for app audio if available. Otherwise place separately exported Cartesia lines against their first visible captions, match the feedback cue to the card, and keep café ambience below both voices. Use the phone tap and first in-glasses transition to align picture, then check the learner’s visible speech, the retry cue and the ending for drift. Camera-only b-roll can cut over the room audio.

The cached Dynamic opening tracks are source media for the app. The screen recording shows what actually ran and how the learner interacted with it. Keep the two kinds of media in separately named bins.

Watch the final cut at normal speed. Confirm three visibly different café actions, a readable retry followed by success, a readable summary, and clear learner and clerk speech in both channels. Verify that the app footage is upright, the framing and feathered edges look right, and the export has no added corner text.
