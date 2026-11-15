# Saved review words and phrases

Issue #23. Branch `codex/demo-review-vocabulary`, base `777642c`. Own the saved-session vocabulary projection and its tests in `portal/records/`, and the review and vocabularies sections of `tools/android-dev/tenin/{TeninActivity.kt,TeninScreens.kt,preview/TeninSnapshot.kt}`. Scene preparation belongs to the dictation stream.

Saved-session detail gets an optional vocabulary list per line. Each match is a half-open UTF-16 span of the recorded target. An exact main target ID selects the vocabulary from its saved beat, which also works after a draft expires. Reviewed word boundaries supply romaji. Other lines use Japanese word segmentation and confirm the authored kana pair before displaying a match. Legacy sessions without the required metadata still show their phrases. Tenin uses separate Japanese-first words and phrases tabs and offers Android Japanese text-to-speech for individual stored readings.
