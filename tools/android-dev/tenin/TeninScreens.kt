package com.immersethebay.phone

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.text.Editable
import android.text.SpannableStringBuilder
import android.text.Spanned
import android.text.TextWatcher
import android.text.style.ForegroundColorSpan
import android.text.style.RelativeSizeSpan
import android.text.style.TypefaceSpan
import android.text.style.StyleSpan
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar

/** One line of a conversation. `mine` is false for what the partner says. */
data class TeninPhrase(
    val jp: String, val romaji: String = "", val en: String = "", val mine: Boolean = true,
    val lesson: String = "", val tries: Int = 0, val passed: Boolean = true,
    val words: List<Triple<String, String, String>> = emptyList(), val swapTitle: String = "", val swap: String = "",
)

data class TeninScenario(
    val id: String, val title: String, val jpTitle: String, val blurb: String, val detail: String,
    val icon: TeninIcon, val phrases: List<TeninPhrase>, val startImage: String = "", val steps: Int = 0, val note: String = "",
)

data class TeninSession(
    val id: String, val day: String, val live: Boolean, val title: String, val note: String,
    val headline: String = "", val tip: String = "", val phrases: List<TeninPhrase> = emptyList(),
)

/** Progress row on the photo screens: text plus whether that step has finished. */
data class TeninStep(val label: CharSequence, val done: Boolean)

private class Waveform(context: Context) : View(context) {
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = Tenin.PRIMARY }
    private val bars = floatArrayOf(.2f, .42f, .7f, 1f, .58f, .8f, .36f, .95f, .5f, .3f, .7f, .18f)
    private val rect = RectF()
    /** 0..1 microphone level; bars keep their relative shape. */
    var level = 1f
        set(value) { field = value.coerceIn(0.15f, 1f); invalidate() }

    override fun onDraw(canvas: Canvas) {
        val slot = width / bars.size.toFloat()
        val thick = slot * 0.5f
        bars.forEachIndexed { i, bar ->
            val h = height * bar * level
            rect.set(i * slot + (slot - thick) / 2, (height - h) / 2, i * slot + (slot + thick) / 2, (height + h) / 2)
            canvas.drawRoundRect(rect, thick / 2, thick / 2, paint)
        }
    }
}

/**
 * Stateless views for every Tenin screen. Each function takes the data to show and callbacks,
 * so the lesson workflow (photo, speech, portal, glasses) can be wired in without touching layout.
 */
class TeninScreens(private val context: Context) {
    val ui = TeninUi(context)
    private fun dp(value: Float) = context.dp(value)
    private fun wrapRow(views: List<View>) = HorizontalScrollView(context).apply {
        isHorizontalScrollBarEnabled = false
        addView(ui.row(*views.toTypedArray()))
    }

    // 1 · Start
    fun start(connected: Boolean, premadeNames: List<String>, onLive: () -> Unit, onPremade: () -> Unit, nav: View) = ui.run {
        fun option(kind: TeninIcon, default: Boolean, name: String, blurb: String, chips: List<String>, action: () -> Unit) = card(
            row(tile(kind).margins(right = 10f), *(if (default) arrayOf<View>(pill("Default", Tenin.WHITE, Tenin.PRIMARY, 14f)) else emptyArray()),
                gap(), icon(TeninIcon.CHEVRON, 22f, Tenin.PRIMARY, 2f)).fillWidth(),
            text(name, 28f).margins(top = 14f),
            body(blurb).margins(top = 8f),
            wrapRow(chips.map { pill(it).margins(right = 8f) }).margins(top = 14f),
            selected = default,
        ).onTap(action)
        screen(listOf(
            header(connected = connected),
            title("What would you like to practice?").margins(top = 18f),
            option(TeninIcon.CAMERA, true, "Live Lesson", "You’re somewhere real. Add a photo or say your goal, then rehearse before you speak.", listOf("Photo", "Say my goal"), onLive).margins(top = 22f),
            option(TeninIcon.PLAY, false, "Pre-made Lesson", "Pick a scenario and practice at home. It all plays in your glasses.", premadeNames, onPremade).margins(top = 18f),
        ), nav = nav)
    }

    // 2 · Choose a scenario
    fun chooseScenario(connected: Boolean, scenarios: List<TeninScenario>, selected: Int, onSelect: (Int) -> Unit, onOpen: (Int) -> Unit, onStart: () -> Unit, onBack: () -> Unit, nav: View) = ui.run {
        val items = scenarios.mapIndexed { i, s ->
            val on = i == selected
            card(row(
                tile(s.icon, 48f, if (on) Tenin.PRIMARY else Tenin.LAVENDER, if (on) Tenin.WHITE else Tenin.PRIMARY).margins(right = 14f),
                column(
                    text(SpannableStringBuilder(s.title).apply {
                        if (s.jpTitle.isNotEmpty()) {
                            val from = length
                            append("  ${s.jpTitle}")
                            listOf(ForegroundColorSpan(Tenin.MUTED), RelativeSizeSpan(0.74f), TypefaceSpan(Tenin.font(context)))
                                .forEach { setSpan(it, from, length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE) }
                        }
                    }, 19f, Tenin.INK, 600),
                    text(s.blurb, 16f, Tenin.MUTED).margins(top = 3f).apply { maxLines = 2 },
                ).weighted(),
                FrameLayout(context).apply {
                    background = rounded(0, dp(22f).toFloat(), if (on) Tenin.PRIMARY else 0xFFC9C4DE.toInt(), dp(1.5f))
                    layoutParams = LinearLayout.LayoutParams(dp(22f), dp(22f)).apply { leftMargin = dp(10f) }
                    if (on) addView(View(context).apply { background = rounded(Tenin.PRIMARY, dp(10f).toFloat()) }, FrameLayout.LayoutParams(dp(10f), dp(10f), Gravity.CENTER))
                    contentDescription = if (on) "Selected" else "Select ${s.title}"
                }.onTap { onSelect(i) },
            ).fillWidth(), selected = on, padding = 16f).margins(top = 11f).onTap { if (on) onOpen(i) else onSelect(i) }
        }
        screen(listOf(header(onBack, connected = connected), title("Choose a scenario").margins(top = 18f, bottom = 9f)) + items +
            listOfNotNull(if (scenarios.isEmpty()) body("No lessons are installed yet.").margins(top = 12f) else text("Tap the selected lesson to see its key phrases.", 15f, Tenin.FAINT).margins(top = 14f)),
            footer = listOf(primaryButton("Start in glasses", TeninIcon.GLASSES, if (scenarios.isEmpty()) null else onStart)), nav = nav)
    }

    private fun phraseRows(phrases: List<TeninPhrase>, onSpeak: (TeninPhrase) -> Unit) = ui.run {
        card(*phrases.flatMapIndexed { i, p ->
            listOfNotNull(if (i > 0) divider().margins(top = 14f, bottom = 14f) else null, row(
                column(eyebrow(if (p.mine) "You’ll say" else "You’ll hear"), text(p.jp, 22f).margins(top = 4f), text(p.en, 16f, Tenin.MUTED).margins(top = 2f)).weighted(),
                roundButton(TeninIcon.SPEAKER) { onSpeak(p) }.margins(left = 10f).apply { contentDescription = "Listen" },
            ).fillWidth())
        }.toTypedArray(), padding = 18f)
    }

    // 3 · Scenario detail
    fun scenarioDetail(connected: Boolean, s: TeninScenario, onSpeak: (TeninPhrase) -> Unit, onStart: () -> Unit, onBack: () -> Unit) = ui.run {
        screen(listOfNotNull(
            header(onBack, connected = connected),
            row(tile(s.icon, 56f, Tenin.PRIMARY, Tenin.WHITE, 16f).margins(right = 14f),
                column(text(s.title, 32f).apply { setLineSpacing(0f, 0.95f) }, text(s.jpTitle, 15f, Tenin.MUTED).margins(top = 2f)).weighted()).fillWidth().margins(top = 18f),
            body(s.detail).margins(top = 14f),
            eyebrow("Key phrases").margins(top = 20f, bottom = 12f),
            phraseRows(s.phrases, onSpeak),
            if (s.note.isEmpty()) null else card(row(icon(TeninIcon.SPARKLE, 20f, Tenin.AMBER).margins(right = 10f), text(s.note, 17f, Tenin.BROWN).weighted()).fillWidth(),
                fill = Tenin.YELLOW_BG, line = Tenin.YELLOW_LINE, padding = 16f, radius = 18f).margins(top = 16f),
        ), footer = listOf(primaryButton("Start in glasses", TeninIcon.GLASSES, onStart)))
    }

    // 2 · Live photo or voice
    fun liveLesson(connected: Boolean, onPhoto: () -> Unit, onLibrary: () -> Unit, onVoice: () -> Unit, onBack: () -> Unit, nav: View) = ui.run {
        screen(listOf(
            header(onBack, connected = connected),
            title("Live Lesson").margins(top = 18f),
            body("Add a photo of your learning environment, or tell Tenin what you want to do.").margins(top = 12f),
            card(
                FrameLayout(context).apply {
                    background = rounded(Tenin.PRIMARY, dp(20f).toFloat())
                    elevation = dp(6f).toFloat()
                    layoutParams = LinearLayout.LayoutParams(dp(64f), dp(64f)).apply { gravity = Gravity.CENTER_HORIZONTAL }
                    addView(ImageView(context).apply { setImageDrawable(TeninIconDrawable(TeninIcon.CAMERA, Tenin.WHITE, 2f)) }, FrameLayout.LayoutParams(dp(30f), dp(30f), Gravity.CENTER))
                },
                text("Take a photo", 26f, Tenin.PRIMARY_DARK).margins(top = 14f).apply { gravity = Gravity.CENTER }.fillWidth(),
                text("Restaurant, shop, counter, clinic…", 16f, Tenin.MUTED).margins(top = 6f).apply { gravity = Gravity.CENTER }.fillWidth(),
                fill = Tenin.TINT, line = 0xFFBDB0EC.toInt(), dash = true,
            ).margins(top = 18f).onTap(onPhoto),
            secondaryButton("Choose from library", TeninIcon.IMAGE, onLibrary).margins(top = 14f),
            card(row(
                FrameLayout(context).apply {
                    background = rounded(Tenin.PRIMARY, dp(46f).toFloat())
                    layoutParams = LinearLayout.LayoutParams(dp(46f), dp(46f)).apply { rightMargin = dp(14f) }
                    addView(ImageView(context).apply { setImageDrawable(TeninIconDrawable(TeninIcon.MIC, Tenin.WHITE, 2f)) }, FrameLayout.LayoutParams(dp(22f), dp(22f), Gravity.CENTER))
                },
                column(text("Say my goal", 20f, Tenin.PRIMARY_DARK, 600), text("e.g. “I want to order food, no shrimp”", 16f, Tenin.MUTED).margins(top = 2f)).weighted(),
            ).fillWidth(), fill = Tenin.YELLOW_BG, line = Tenin.YELLOW_LINE, padding = 16f, radius = 20f).margins(top = 14f).onTap(onVoice),
            card(row(icon(TeninIcon.SHIELD, 22f, Tenin.GREEN).margins(right = 12f),
                column(text("Your photo is used for this lesson", 18f, 0xFF134D35.toInt(), 600),
                    text("It is sent to build the lesson and removed from the server within 30 minutes.", 16f, Tenin.GREEN).margins(top = 3f)).weighted(),
                gravity = Gravity.TOP).fillWidth(), fill = Tenin.GREEN_BG, line = Tenin.GREEN_LINE, padding = 16f, radius = 20f).margins(top = 14f),
        ), nav = nav)
    }

    // 3 · Say my goal listening
    /** `busy`: the recording is being processed; the mic is greyed out and cannot be tapped again. */
    fun sayGoal(connected: Boolean, transcript: String, listening: Boolean, micLevel: Float, onMic: () -> Unit, onClose: () -> Unit, busy: Boolean = false) = ui.run {
        screen(listOf(
            header(onClose, close = true, connected = connected),
            title("What do you need to do?").margins(top = 18f),
            body("Speak in any language. Tenin will set up the lesson.").margins(top = 12f),
            card(eyebrow("You said"),
                text(SpannableStringBuilder(if (transcript.isEmpty()) "…" else "“$transcript").apply {
                    if (transcript.isNotEmpty() && listening) append("…", ForegroundColorSpan(Tenin.PRIMARY), Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
                    if (transcript.isNotEmpty()) append("”")
                }, 26f).margins(top = 8f),
                fill = Tenin.TINT).margins(top = 16f),
        ), footer = listOf(
            Waveform(context).apply {
                level = if (listening) micLevel else 0.15f
                layoutParams = LinearLayout.LayoutParams(dp(128f), dp(56f)).apply { gravity = Gravity.CENTER_HORIZONTAL; bottomMargin = dp(12f) }
            },
            FrameLayout(context).apply {
                background = rounded(if (busy) 0xFFEDEBF3.toInt() else Tenin.LAVENDER, dp(108f).toFloat())
                layoutParams = LinearLayout.LayoutParams(dp(108f), dp(108f)).apply { gravity = Gravity.CENTER_HORIZONTAL }
                contentDescription = if (busy) "Working on it" else if (listening) "Finish speaking" else "Start speaking"
                isEnabled = !busy
                addView(FrameLayout(context).apply {
                    background = rounded(if (busy) 0xFFC9C5D6.toInt() else Tenin.PRIMARY, dp(88f).toFloat())
                    addView(ImageView(context).apply { setImageDrawable(TeninIconDrawable(TeninIcon.MIC, Tenin.WHITE, 2f)) }, FrameLayout.LayoutParams(dp(36f), dp(36f), Gravity.CENTER))
                }, FrameLayout.LayoutParams(dp(88f), dp(88f), Gravity.CENTER))
            }.let { if (busy) it else it.onTap(onMic) },
            text(if (busy) "Working on it…" else if (listening) "Listening… tap to finish" else "Tap to speak", 17f,
                if (busy) Tenin.MUTED else Tenin.PRIMARY_DARK, 600).margins(top = 10f).apply { gravity = Gravity.CENTER }.fillWidth(),
        ))
    }

    private fun photo(bitmap: Bitmap?) = ImageView(context).apply {
        background = rounded(0xFFD9D5E8.toInt(), dp(24f).toFloat())
        clipToOutline = true
        scaleType = ImageView.ScaleType.CENTER_CROP
        if (bitmap != null) setImageBitmap(bitmap)
        contentDescription = "Your photo"
        layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(250f))
    }

    private fun checklist(steps: List<TeninStep>, outlined: Boolean = false) = ui.run {
        card(*steps.flatMapIndexed { i, step ->
            listOfNotNull(if (i > 0) divider().margins(top = 12f, bottom = 12f) else null, row(
                if (step.done && outlined) icon(TeninIcon.CHECK_CIRCLE, 22f, Tenin.GREEN).margins(right = 12f)
                else if (step.done) FrameLayout(context).apply {
                    background = rounded(Tenin.GREEN, dp(26f).toFloat())
                    layoutParams = LinearLayout.LayoutParams(dp(26f), dp(26f)).apply { rightMargin = dp(12f) }
                    addView(ImageView(context).apply { setImageDrawable(TeninIconDrawable(TeninIcon.CHECK, Tenin.WHITE, 2.6f)) }, FrameLayout.LayoutParams(dp(14f), dp(14f), Gravity.CENTER))
                } else ProgressBar(context).apply {
                    indeterminateTintList = android.content.res.ColorStateList.valueOf(Tenin.PRIMARY)
                    layoutParams = LinearLayout.LayoutParams(dp(26f), dp(26f)).apply { rightMargin = dp(12f) }
                },
                text(step.label, 18f, Tenin.INK, if (step.done) 400 else 600).weighted(),
            ).fillWidth())
        }.toTypedArray(), padding = 18f)
    }

    /** "Lesson ready: **Ordering food**" style label. */
    fun emphasised(prefix: String, strong: String): CharSequence = SpannableStringBuilder(prefix).apply {
        append(strong, StyleSpan(android.graphics.Typeface.BOLD), Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
    }

    // 4 · Reading your photo and 5 · Photo ready (ready = every step done)
    fun photoProgress(connected: Boolean, bitmap: Bitmap?, steps: List<TeninStep>, ready: Boolean, error: String, onContinue: () -> Unit, onRetake: () -> Unit, onCancel: () -> Unit, onBack: () -> Unit) = ui.run {
        screen(listOfNotNull(
            header(onBack, connected = connected),
            title(if (error.isNotEmpty()) "That didn’t work" else if (ready) "Photo ready" else "Reading your photo").margins(top = 18f),
            photo(bitmap).margins(top = 16f),
            if (error.isNotEmpty()) card(text(error, 17f, Tenin.BROWN), fill = Tenin.YELLOW_BG, line = Tenin.YELLOW_LINE, padding = 16f, radius = 18f).margins(top = 16f)
            else checklist(steps).margins(top = 16f),
        ), footer = when {
            error.isNotEmpty() -> listOf(primaryButton("Try again", TeninIcon.RETRY, onContinue), secondaryButton("Retake", null, onRetake).margins(top = 10f))
            ready -> listOf(primaryButton("Continue in glasses", TeninIcon.GLASSES, onContinue), secondaryButton("Retake", null, onRetake).margins(top = 10f))
            else -> listOf(busyButton("Getting your lesson ready…"), secondaryButton("Cancel", null, onCancel).margins(top = 10f))
        })
    }

    // 4 · Here's your lesson (after a spoken goal)
    fun lessonSummary(connected: Boolean, situationIcon: TeninIcon, situation: String, remember: String, practice: String, onAddPhoto: (() -> Unit)?, onContinue: () -> Unit, onAgain: () -> Unit, onBack: () -> Unit) = ui.run {
        fun item(kind: TeninIcon, fill: Int, color: Int, label: String, value: String) = row(
            tile(kind, 42f, fill, color, 12f).margins(right = 14f), column(eyebrow(label), text(value, 19f, Tenin.INK, 600).margins(top = 2f)).weighted()).fillWidth()
        val rows = listOfNotNull(
            item(situationIcon, Tenin.LAVENDER, Tenin.PRIMARY, "Situation", situation),
            if (remember.isEmpty()) null else item(TeninIcon.ALERT, Tenin.YELLOW_BG, Tenin.AMBER, "Remember", remember),
            if (practice.isEmpty()) null else item(TeninIcon.SPARKLE, Tenin.LAVENDER, Tenin.PRIMARY, "You’ll practice", practice),
        ).flatMapIndexed { i, v -> listOfNotNull(if (i > 0) divider().margins(top = 14f, bottom = 14f) else null, v) }
        screen(listOfNotNull(
            header(onBack, connected = connected),
            title("Here’s your lesson").margins(top = 18f),
            card(*rows.toTypedArray(), padding = 18f).margins(top = 16f),
            if (onAddPhoto == null) null else card(row(icon(TeninIcon.CAMERA, 22f, Tenin.PRIMARY).margins(right = 14f),
                column(text("Add a photo too", 19f, Tenin.PRIMARY_DARK, 600), text("Optional: makes the scene look like where you are", 15f, Tenin.MUTED).margins(top = 2f)).weighted()).fillWidth(),
                fill = Tenin.BG, dash = true, padding = 18f).margins(top = 16f).onTap(onAddPhoto),
        ), footer = listOf(primaryButton("Continue in glasses", TeninIcon.GLASSES, onContinue), secondaryButton("Say it again", null, onAgain).margins(top = 10f)))
    }

    // 6 · Put on your glasses
    // `warning` (e.g. low battery) shows above the checklist. Starting waits until the glasses are connected.
    fun putOnGlasses(connected: Boolean, lessonTitle: String, warning: String, onWearing: () -> Unit, onBack: () -> Unit) = ui.run {
        screen(listOfNotNull(
            header(onBack),
            FrameLayout(context).apply {
                background = rounded(0, dp(196f).toFloat(), 0xFFCFC6F0.toInt(), dp(1.5f), dp(4f).toFloat())
                layoutParams = LinearLayout.LayoutParams(dp(196f), dp(196f)).apply { gravity = Gravity.CENTER_HORIZONTAL; topMargin = dp(26f) }
                addView(FrameLayout(context).apply {
                    background = rounded(Tenin.LAVENDER, dp(168f).toFloat())
                    addView(ImageView(context).apply { setImageDrawable(TeninIconDrawable(TeninIcon.GLASSES, Tenin.PRIMARY, 1.9f)) }, FrameLayout.LayoutParams(dp(84f), dp(84f), Gravity.CENTER))
                }, FrameLayout.LayoutParams(dp(168f), dp(168f), Gravity.CENTER))
            },
            title("Put on your glasses").margins(top = 22f).apply { gravity = Gravity.CENTER }.fillWidth(),
            body(if (connected) "Your lesson is ready. Tap the button below once they’re on." else "Plug the glasses into your phone, then put them on.").margins(top = 10f).apply { gravity = Gravity.CENTER }.fillWidth(),
            if (warning.isEmpty()) null else card(row(icon(TeninIcon.ALERT, 22f, Tenin.AMBER).margins(right = 12f), text(warning, 17f, Tenin.BROWN).weighted(), gravity = Gravity.TOP).fillWidth(),
                fill = Tenin.YELLOW_BG, line = Tenin.YELLOW_LINE, padding = 16f, radius = 18f).margins(top = 18f),
            checklist(listOf(
                TeninStep(if (connected) "Glasses connected" else "Waiting for the glasses…", connected),
                TeninStep("Scene ready: $lessonTitle", true),
                TeninStep("The glasses listen once the lesson starts", true),
            ), outlined = true).margins(top = 22f),
        ), footer = listOf(if (connected) primaryButton("I’m wearing them", TeninIcon.EYE, onWearing) else busyButton("Waiting for your glasses…")))
    }

    // The lesson ended without End lesson: the glasses disconnected or the wearer left the glasses view.
    fun lessonStopped(title: String, detail: String, onAgain: () -> Unit, onDone: () -> Unit) = ui.run {
        screen(listOf(
            header(),
            FrameLayout(context).apply {
                background = rounded(Tenin.YELLOW_BG, dp(92f).toFloat())
                layoutParams = LinearLayout.LayoutParams(dp(92f), dp(92f)).apply { gravity = Gravity.CENTER_HORIZONTAL; topMargin = dp(26f) }
                addView(ImageView(context).apply { setImageDrawable(TeninIconDrawable(TeninIcon.ALERT, Tenin.AMBER, 2.2f)) }, FrameLayout.LayoutParams(dp(44f), dp(44f), Gravity.CENTER))
            },
            text(title, 34f).margins(top = 14f).apply { gravity = Gravity.CENTER }.fillWidth(),
            body(detail).margins(top = 10f, left = 8f, right = 8f).apply { gravity = Gravity.CENTER }.fillWidth(),
        ), footer = listOf(primaryButton("Start again", TeninIcon.GLASSES, onAgain), secondaryButton("Done", null, onDone).margins(top = 10f)))
    }

    // 7 · Lesson running. step = 0 hides the progress bar when the glasses do not report progress.
    fun running(connected: Boolean, lessonTitle: String, step: Int, steps: Int, onPause: (() -> Unit)?, onEnd: () -> Unit) = ui.run {
        screen(listOfNotNull(
            header(connected = connected, onDark = true),
            FrameLayout(context).apply {
                background = rounded(0x22FFFFFF, dp(132f).toFloat(), 0x55FFFFFF, dp(1f))
                layoutParams = LinearLayout.LayoutParams(dp(132f), dp(132f)).apply { gravity = Gravity.CENTER_HORIZONTAL; topMargin = dp(70f) }
                addView(ImageView(context).apply { setImageDrawable(TeninIconDrawable(TeninIcon.GLASSES, Tenin.WHITE, 1.9f)) }, FrameLayout.LayoutParams(dp(64f), dp(64f), Gravity.CENTER))
            },
            text("Lesson running\nin your glasses", 34f, Tenin.WHITE).margins(top = 24f).apply { gravity = Gravity.CENTER; setLineSpacing(0f, 1f) }.fillWidth(),
            text("You can put your phone away. Everything happens in the glasses.", 18f, Tenin.WHITE).margins(top = 14f, left = 16f, right = 16f).apply { gravity = Gravity.CENTER }.fillWidth(),
            row(text(lessonTitle, 17f, Tenin.WHITE).weighted(), text(if (step > 0 && steps > 0) "Step $step of $steps" else "", 17f, Tenin.WHITE)).fillWidth().margins(top = 40f),
            if (step > 0 && steps > 0) LinearLayout(context).apply {
                background = rounded(0x44FFFFFF, dp(8f).toFloat())
                layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(8f)).apply { topMargin = dp(10f) }
                addView(View(context).apply { background = rounded(Tenin.YELLOW, dp(8f).toFloat()) }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, step.coerceAtMost(steps).toFloat()))
                addView(View(context), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, (steps - step).coerceAtLeast(0).toFloat()))
            } else null,
        ), footer = listOf(row(*listOfNotNull(
            if (onPause == null) null else row(icon(TeninIcon.PAUSE, 22f, Tenin.WHITE, 2f).margins(right = 10f), text("Pause", 19f, Tenin.WHITE, 600), gravity = Gravity.CENTER).apply {
                background = rounded(0x22FFFFFF, dp(18f).toFloat(), 0x66FFFFFF, dp(1f)); minimumHeight = dp(54f)
            }.weighted().margins(right = 10f).onTap(onPause),
            row(icon(TeninIcon.STOP, 22f, Tenin.PRIMARY_DARK, 2f).margins(right = 10f), text("End lesson", 19f, Tenin.PRIMARY_DARK, 600), gravity = Gravity.CENTER).apply {
                background = rounded(Tenin.WHITE, dp(18f).toFloat()); minimumHeight = dp(54f)
            }.weighted().onTap(onEnd),
        ).toTypedArray()).fillWidth()), fill = Tenin.PRIMARY)
    }

    // 8 · Lesson complete
    fun complete(summary: String, saved: Int, review: Int, tip: String, onPhrases: () -> Unit, onDone: () -> Unit) = ui.run {
        fun stat(value: Int, label: String, color: Int) = card(text(value.toString(), 38f, color, 500), text(label, 16f, Tenin.MUTED), padding = 16f)
        screen(listOfNotNull(
            header(),
            FrameLayout(context).apply {
                background = rounded(Tenin.GREEN_BG, dp(92f).toFloat())
                layoutParams = LinearLayout.LayoutParams(dp(92f), dp(92f)).apply { gravity = Gravity.CENTER_HORIZONTAL; topMargin = dp(26f) }
                addView(ImageView(context).apply { setImageDrawable(TeninIconDrawable(TeninIcon.CHECK, Tenin.GREEN, 2.4f)) }, FrameLayout.LayoutParams(dp(46f), dp(46f), Gravity.CENTER))
            },
            text("You did it!", 38f).margins(top = 14f).apply { gravity = Gravity.CENTER }.fillWidth(),
            body(summary).margins(top = 8f).apply { gravity = Gravity.CENTER }.fillWidth(),
            row(stat(saved, if (saved == 1) "phrase saved" else "phrases saved", Tenin.PRIMARY).weighted().margins(right = 6f),
                stat(review, "to review later", Tenin.AMBER).weighted().margins(left = 6f)).fillWidth().margins(top = 18f),
            if (tip.isEmpty()) null else card(row(icon(TeninIcon.SPARKLE, 22f, Tenin.AMBER).margins(right = 12f), text("Tip: $tip", 18f, Tenin.BROWN).weighted()).fillWidth(),
                fill = Tenin.YELLOW_BG, line = Tenin.YELLOW_LINE, padding = 18f).margins(top = 16f),
        ), footer = listOf(primaryButton("See my phrases", TeninIcon.BOOK, onPhrases), secondaryButton("Done", null, onDone).margins(top = 10f)))
    }

    // Review tab
    fun review(due: Int, sessions: List<TeninSession>, status: String, onReview: () -> Unit, onOpen: (TeninSession) -> Unit, nav: View) = ui.run {
        val items = sessions.map { s ->
            card(row(column(
                row(text(s.day, 15f, Tenin.MUTED).margins(right = 8f), pill(if (s.live) "Live" else "Pre-made", size = 13f)),
                text(s.title, 26f).margins(top = 6f),
                text(s.note, 16f, Tenin.MUTED).margins(top = 4f),
            ).weighted(), icon(TeninIcon.CHEVRON, 22f, Tenin.PRIMARY, 2f)).fillWidth(), padding = 18f).margins(top = 11f).onTap { onOpen(s) }
        }
        screen(listOfNotNull(
            header(),
            title("Review").margins(top = 14f),
            if (due > 0) card(row(
                text(due.toString(), 28f, Tenin.BROWN, 500).apply { background = rounded(Tenin.YELLOW, dp(16f).toFloat()); gravity = Gravity.CENTER; layoutParams = LinearLayout.LayoutParams(dp(56f), dp(56f)).apply { rightMargin = dp(16f) } },
                column(text(if (due == 1) "Phrase due for review" else "Phrases due for review", 19f, Tenin.PRIMARY_DARK, 600),
                    row(icon(TeninIcon.GLASSES, 18f, Tenin.WHITE, 2f).margins(right = 8f), text("Review in glasses", 16f, Tenin.WHITE, 600)).apply {
                        background = rounded(Tenin.PRIMARY, dp(12f).toFloat()); setPadding(dp(14f), dp(9f), dp(14f), dp(10f))
                    }.margins(top = 8f).onTap(onReview)).weighted(),
            ).fillWidth(), fill = Tenin.LAVENDER, line = Tenin.LAVENDER, padding = 18f).margins(top = 16f) else null,
            eyebrow("Past sessions").margins(top = 22f, bottom = 3f),
        ) + items + listOfNotNull(if (status.isNotEmpty()) body(status).margins(top = 12f) else null), nav = nav)
    }

    // Session detail
    fun sessionDetail(s: TeninSession, onPracticeTricky: (() -> Unit)?, onReplay: () -> Unit, onBack: () -> Unit, nav: View) = ui.run {
        val rows = s.phrases.filter { it.mine }.flatMapIndexed { i, p ->
            val ok = p.passed && p.tries <= 1
            listOfNotNull(if (i > 0) divider().margins(top = 14f, bottom = 14f) else null, row(
                icon(if (ok) TeninIcon.CHECK_CIRCLE else TeninIcon.RETRY, 22f, if (ok) Tenin.GREEN else 0xFFA34A00.toInt()).margins(right = 12f),
                column(text(p.jp, 20f), text(p.en, 16f, Tenin.MUTED).margins(top = 2f)).weighted(),
                (if (ok) pill("Understood", Tenin.GREEN, Tenin.GREEN_BG) else pill(if (p.tries > 1) "${p.tries} tries" else "Not yet", Tenin.BROWN, Tenin.YELLOW_BG)).margins(left = 8f),
            ).fillWidth())
        }
        screen(listOfNotNull(
            header(onBack),
            eyebrow("${s.day} · ${if (s.live) "Live lesson" else "Pre-made lesson"}").margins(top = 18f),
            title(s.title).margins(top = 2f),
            if (s.headline.isEmpty() && s.tip.isEmpty()) null else card(row(icon(TeninIcon.SPARKLE, 22f, Tenin.AMBER).margins(right = 12f, top = 4f), column(
                *listOfNotNull(if (s.headline.isEmpty()) null else text(s.headline, 23f, Tenin.PRIMARY_DARK), if (s.tip.isEmpty()) null else text("Tip: ${s.tip}", 16f, Tenin.MUTED).margins(top = 4f)).toTypedArray(),
            ).weighted(), gravity = Gravity.TOP).fillWidth(), fill = Tenin.YELLOW_BG, line = Tenin.YELLOW_LINE, padding = 18f).margins(top = 14f),
            eyebrow("Phrases you used").margins(top = 22f, bottom = 12f),
            if (rows.isEmpty()) body("No phrases were recorded in this session.") else card(*rows.toTypedArray(), padding = 18f),
            if (onPracticeTricky == null) null else primaryButton(if (s.phrases.count { it.mine && !(it.passed && it.tries <= 1) } == 1) "Practice the tricky one" else "Practice the tricky ones", TeninIcon.GLASSES, onPracticeTricky).margins(top = 20f),
            secondaryButton("Replay the scene", null, onReplay).margins(top = 10f),
        ), nav = nav)
    }

    private fun romaji(value: String, size: Float) = ui.text(value, size, Tenin.BROWN, 500, italic = true).apply {
        background = rounded(Tenin.YELLOW, dp(6f).toFloat())
        setPadding(dp(7f), dp(1f), dp(7f), dp(3f))
    }

    // Vocabularies tab
    fun vocabularies(phrases: List<TeninPhrase>, lessons: List<String>, filter: String, query: String, status: String, onFilter: (String) -> Unit, onQuery: (String) -> Unit, onSpeak: (TeninPhrase) -> Unit, onOpen: (TeninPhrase) -> Unit, nav: View) = ui.run {
        val rows = phrases.flatMapIndexed { i, p ->
            listOfNotNull(if (i > 0) divider().margins(top = 14f, bottom = 14f) else null, row(
                column(*listOfNotNull(text(p.jp, 21f), if (p.romaji.isEmpty()) null else romaji(p.romaji, 17f).margins(top = 5f),
                    text(listOf(p.en, p.lesson).filter { it.isNotEmpty() }.joinToString(" · "), 16f, Tenin.MUTED).margins(top = 5f)).toTypedArray()).weighted().onTap { onOpen(p) },
                roundButton(TeninIcon.SPEAKER, 44f) { onSpeak(p) }.margins(left = 10f).apply { contentDescription = "Listen" },
            ).fillWidth())
        }
        screen(listOfNotNull(
            header(),
            title("Vocabularies").margins(top = 14f),
            row(icon(TeninIcon.SEARCH, 20f, Tenin.MUTED, 2f).margins(right = 10f), EditText(context).apply {
                setText(query); setSelection(query.length)
                hint = "Search phrases"
                background = null
                typeface = Tenin.font(context)
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 18f * Tenin.scale(context))
                setTextColor(Tenin.INK); setHintTextColor(Tenin.FAINT)
                setSingleLine()
                setPadding(0, dp(12f), 0, dp(12f))
                addTextChangedListener(object : TextWatcher {
                    override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
                    override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
                    override fun afterTextChanged(s: Editable?) { if (s.toString() != query) onQuery(s.toString()) }
                })
            }.weighted()).apply { background = rounded(Tenin.WHITE, dp(18f).toFloat(), Tenin.LINE, dp(1f)); setPadding(dp(16f), 0, dp(16f), 0) }.fillWidth().margins(top = 14f),
            wrapRow((listOf("") + lessons).map { name ->
                val on = name == filter
                pill(if (name.isEmpty()) "All" else name, if (on) Tenin.WHITE else Tenin.PRIMARY_DARK, if (on) Tenin.PRIMARY else Tenin.WHITE, 16f).apply {
                    if (!on) background = rounded(Tenin.WHITE, dp(40f).toFloat(), Tenin.LINE, dp(1f))
                    setPadding(dp(15f), dp(8f), dp(15f), dp(9f))
                }.margins(right = 8f).onTap { onFilter(name) }
            }).margins(top = 16f),
            if (rows.isEmpty()) body(if (status.isNotEmpty()) status else "Phrases you practice will be saved here.").margins(top = 18f)
            else card(*rows.toTypedArray(), padding = 18f).margins(top = 18f),
        ), nav = nav)
    }

    // Phrase detail
    fun phraseDetail(p: TeninPhrase, onListen: () -> Unit, onSlowly: () -> Unit, onPractice: (() -> Unit)?, onBack: () -> Unit, nav: View) = ui.run {
        fun chip(label: String, kind: TeninIcon, solid: Boolean, action: () -> Unit) = row(icon(kind, 20f, if (solid) Tenin.PRIMARY_DARK else Tenin.WHITE, 2f).margins(right = 8f), text(label, 17f, if (solid) Tenin.PRIMARY_DARK else Tenin.WHITE, 600)).apply {
            background = rounded(if (solid) Tenin.WHITE else 0, dp(12f).toFloat(), 0x77FFFFFF, if (solid) 0 else dp(1f))
            setPadding(dp(16f), dp(10f), dp(16f), dp(11f))
        }.onTap(action)
        screen(listOfNotNull(
            header(onBack),
            card(*listOfNotNull(
                if (p.lesson.isEmpty()) null else eyebrow(p.lesson, 0xFFE9E3FB.toInt()),
                text(p.jp, 30f, Tenin.WHITE).margins(top = 6f),
                if (p.romaji.isEmpty()) null else romaji(p.romaji, 24f).margins(top = 8f),
                text(p.en, 18f, Tenin.WHITE).margins(top = 8f),
                row(chip("Listen", TeninIcon.SPEAKER, true, onListen).margins(right = 8f), chip("Slowly", TeninIcon.TIMER, false, onSlowly)).margins(top = 16f),
            ).toTypedArray(), fill = Tenin.PRIMARY, line = Tenin.PRIMARY, padding = 22f).apply { elevation = dp(6f).toFloat() }.margins(top = 18f),
            if (p.words.isEmpty()) null else eyebrow("Word by word").margins(top = 22f, bottom = 12f),
            if (p.words.isEmpty()) null else card(*p.words.flatMapIndexed { i, (word, reading, meaning) ->
                listOfNotNull(if (i > 0) divider().margins(top = 14f, bottom = 14f) else null, row(
                    text(word, 20f).apply { layoutParams = LinearLayout.LayoutParams(dp(92f), ViewGroup.LayoutParams.WRAP_CONTENT) },
                    text(reading, 18f, Tenin.PRIMARY_DARK, 400, italic = true).apply { layoutParams = LinearLayout.LayoutParams(dp(84f), ViewGroup.LayoutParams.WRAP_CONTENT) },
                    text(meaning, 17f, Tenin.MUTED).weighted(), gravity = Gravity.TOP).fillWidth())
            }.toTypedArray(), padding = 18f),
            if (p.swap.isEmpty()) null else card(text(p.swapTitle, 17f, Tenin.BROWN, 600), text(p.swap, 19f).margins(top = 6f), fill = Tenin.YELLOW_BG, line = Tenin.YELLOW_LINE, padding = 18f).margins(top = 16f),
            if (onPractice == null) null else primaryButton("Practice in glasses", TeninIcon.GLASSES, onPractice).margins(top = 20f),
        ), nav = nav)
    }
}
