# Demo MR caption typography — task 20

Branch: `codex/demo-mr-typography`, based on `3e02168`.

## Scope

Own only `Assets/Scripts/Learning/LearningOverlay.cs`, the existing
`Assets/Scripts/Learning/Editor/LearningVisualChecks.cs`, and this document.
No startup, media, staging, scene, XR, package, or project-setting changes.
The root chat holds the sole Unity build/device lease. This task must not run
Unity or ADB. Local commit only.

## Behavior

The previous layout could shrink Japanese from 42 to 18 and conversation
captions from 26 to 16 as text arrived. Fonts now remain fixed in canvas units:

| Row | Previous nominal size | New fixed size |
| --- | ---: | ---: |
| Japanese target | 42 | 60 |
| Romaji | 29 | 40 |
| English/Chinese translation | 29 | 36 |
| Kana | 29 | 36 |
| Clerk / learner, including English | 26 | 40 |

The 1400-unit canvas uses uniform world scale
`min(videoWorldWidth * 0.88 / 1400, videoWorldHeight / 900)`.
At 16:9, the font em sizes are approximately 6.67%, 4.44%, and 4% of video
height for Japanese, romaji, and translation. These are font em dimensions,
not measured glyph ink heights. Wider video at the same height cannot inflate
text. Narrow video caps the canvas at 88% of video width. Source resolution,
caption length, incoming translation, and conversation state do not alter
font size. Moving the screen farther away still reduces apparent angular size.

The backdrop grows with measured wrapping, to at most 52% of video height,
with its bottom 3% above the video bottom. English follows romaji before kana.
When crowded, hide kana, then prior dialogue while a lesson target is present,
then supplementary status. Requested Japanese/romaji/translation remain
visible. Their visibility switches retain their previous defaults; translation
still requires `SetTranslationVisible(true)` or the existing inspector setting.

Very long external text shares the remaining height, reserving at least one
measured line per visible primary row. It receives an explicit ellipsis at the
same font size instead of shrinking. Text-element boundaries protect surrogate
pairs/combining characters; Japanese highlighting is reapplied after shortening
so color markup stays valid. The backing lesson and dialogue strings remain
complete and are restored on the next refresh before measuring. There is no
paging UI: arbitrarily long text will not be shown in full simultaneously.
The separate controls canvas and interaction behavior are unchanged.

## Verification

Completed in this task:

- Reviewed the diff and checked whitespace with `git diff --check`.
- Checked aspect/scale and minimum-height allocation arithmetic independently.
- Extended the existing `LearningVisualChecks.Run` with Japanese/romaji/English,
  wrapped lessons, highlighted oversized Japanese, bilingual conversation, 21:9,
  and square video cases. It asserts fixed font sizes, uniform scale, primary-row
  visibility, no vertical clipping/row overlap, backdrop containment, and explicit
  overflow ellipsis. It writes six previews under ignored `Logs/`.

**Not run:** Unity compile, the editor visual checks, APK build, or device tests.
No claim of rendered or optical readability verification is made. Existing script
`.meta` files are retained; no new Unity assets were added.

## Build-owner verification

1. After integration, use the root chat's single build lease to compile for
   Android. Require no `error CS`. Run
   `ImmerseTheBay.Learning.Editor.LearningVisualChecks.Run` in a disposable editor
   session (it creates an unsaved scene), then inspect all six `Logs/unity-*.png`
   outputs. Confirm full short captions, legible glyphs, complete highlight tags,
   and the deliberate ellipsis on oversized text. Failures must be fixed before
   calling these checks passed.
2. Build/stage/install through the existing root-owned S25 workflow. On Air 2
   Ultra, run both bundled lessons and a generated photo/goal lesson, enabling
   romaji and English for the typography pass. Confirm Japanese, romaji and
   English are readable without leaning forward, with no missing CJK glyphs,
   overlap, or stretched letters. Compare a short line and a long wrapped line.
3. Let clerk and learner captions and their English translations arrive at
   different times. Confirm no font-size pumping as captions, hints, status,
   and conversation mode change. Confirm optional rows disappear only when
   crowded, while the active target and requested translation stay visible.
4. Observe the native video transition from its seed image, look left/right and
   down/up, and test the existing screen placement. Confirm the video still has
   useful unobstructed context above the captions, the text stays near its lower
   edge, and the separate controls remain usable. Check both eyes in the glasses;
   a phone screenshot or monocular cast cannot establish optical readability.
5. Record device, lesson/line, video aspect, viewing distance, English visibility,
   screenshot/cast reference, and the wearer's readability judgment. The larger
   maximum band (52%, previously 32% lesson / 40% conversation) is the main
   tradeoff requiring a wearer decision.

## Requests for owners

Run the compile, editor rendering, and device checks above under the root lease.
If English must be enabled in a pre-existing serialized scene, its owner should
set the existing translation visibility option; this task does not edit scenes.
