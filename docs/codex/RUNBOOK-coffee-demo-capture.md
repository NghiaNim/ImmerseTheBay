# Record the Order Coffee demo

Use the native Tenin app on the S25 with Air 2 Ultra attached and the development Portal reachable. Keep Voice example enabled. On the phone, open **Courses**, leave **Order Coffee** selected, and tap **Start in glasses**. Pause at **I’m wearing them** while the recordings start.

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
