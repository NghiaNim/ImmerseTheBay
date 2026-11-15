# Simplify Tenin's lesson entry and show authored scene previews

Issues #21 and #22, plus the phone key-phrase and review presentation from #23. Base `1d8a076`, branch `codex/demo-tenin-previews`. Own `tools/android-dev/tenin/TeninActivity.kt`, `TeninScreens.kt`, `preview.sh`, `preview/TeninSnapshot.kt`, and this brief. Consume bundled lesson references and recorded line metadata. Keep the official MR reference untouched.

Render Japanese, existing romaji, then English in the key-phrase list and saved review. Show the scripted partner prompt when present on the saved exchange. Learner outcomes stay attached to the learner line. Older entries without a reading display “Reading unavailable”.

Requests for owners

- Portal records and content need to expose validated sentence spans and learned words through the saved-session detail route. The current route exposes practiced lines and partner prompts, while the replay snapshot keeps beat vocabulary private. Phone Words and Phrases tabs should use that shared metadata.
