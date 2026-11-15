# Live dictation during Tenin scene preparation

Issue #17. Branch `codex/demo-live-goal-dictation`. Own the goal workflow in `tools/android-dev/TeninWorkflow.kt`, its Tenin screens and activity, the speech service visibility declaration in the app-owned Android library manifest, and the equivalent preview manifest.

Use the system `SpeechRecognizer` with explicit Japanese, English, and Chinese language tags and actual partial callbacks. Show the final text as an editable goal before sending it to the lesson generator. Offer typing or a separate record-and-transcribe attempt when the recognizer cannot provide a result. Only one engine owns the microphone. Preserve photo state and the intended permission/picker result. Cancel stale callbacks on Back, backgrounding, or owner change.

The Android speech service may return no partials or end at a pause. This behavior requires multilingual verification on S25. [Android RecognitionListener](https://developer.android.com/reference/android/speech/RecognitionListener#onPartialResults(android.os.Bundle))

## Device acceptance

1. Open Say my goal and select 日本語. Start dictation and speak in two clauses. Observe recognized text while the stop button is still visible. Repeat with English and 中文. End with the Stop button and with a natural pause.
2. Edit a final transcript and tap Prepare lesson. Confirm the resulting situation reflects the edit. Repeat from a photo, with the selected image visible through review and generation.
3. Exercise denial, unavailable network recognition, typing, and Record for transcription. That fallback receives results after Stop and still opens the editable review.
4. During an active take, press Back or Home and reopen the app. Confirm the system microphone indicator turns off and late results leave the current screen unchanged. Reenter and start a fresh take.
