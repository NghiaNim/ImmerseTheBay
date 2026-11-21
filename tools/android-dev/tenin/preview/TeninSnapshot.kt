package com.immersethebay.phone

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Canvas
import android.util.Log
import android.view.ContextThemeWrapper
import android.view.View
import java.io.File

/**
 * Preview build only: draws every Tenin screen with sample content into PNG files without
 * showing anything on the phone, so layouts can be reviewed while the device is in use.
 *   adb shell am broadcast --include-stopped-packages -n com.immersethebay.tenin.preview/com.immersethebay.phone.TeninSnapshot
 *   adb exec-out run-as com.immersethebay.tenin.preview cat files/tenin/start.png > start.png
 */
class TeninSnapshot : BroadcastReceiver() {
    override fun onReceive(app: Context, intent: Intent) {
        val context = ContextThemeWrapper(app, android.R.style.Theme_Material_Light_NoActionBar)
        val s = TeninScreens(context)
        val food = TeninDemo.scenarios.first()
        val sessions = TeninDemo.sessions
        val sampleSession = sessions.first()
        val reviewPhrases = sampleSession.phrases.mapIndexed { index, phrase ->
            when (index) {
                0 -> phrase.copy(partner = TeninPhrase("何にしますか。", "Nani ni shimasu ka?", "What would you like?", mine = false))
                1 -> phrase.copy(romaji = "")
                else -> phrase
            }
        }
        val phrases = sessions.flatMap { session -> session.phrases.map { it.copy(lesson = session.title) } }.distinctBy { it.jp }
        val phraseRows = phrases.map { TeninVocabularyRow(s.highlightedJapanese(it), it.romaji, "${it.en} · ${it.lesson}", {}, {}) }
        val wordRows = phrases.flatMap { it.words }.distinctBy { it.jp }.map { TeninVocabularyRow(it.jp, it.reading, it.en, {}) }
        val lessons = sessions.filter { it.phrases.isNotEmpty() }.distinctBy { it.title }
        fun nav(tab: TeninUi.Tab) = s.ui.bottomNav(tab) {}
        val reading = listOf(TeninStep("Spotted a restaurant", true), TeninStep("Found the menu board", true))
        val screens = linkedMapOf<String, () -> View>(
            "start" to { s.start(true, {}, {}, nav(TeninUi.Tab.START)) },
            "choose" to { s.chooseScenario(true, TeninDemo.scenarios, 0, {}, {}, {}, {}, nav(TeninUi.Tab.START)) },
            "detail" to { s.scenarioDetail(true, food, {}, {}, {}) },
            "live" to { s.liveLesson(true, {}, {}, {}, {}, nav(TeninUi.Tab.START)) },
            "goal" to { s.sayGoal(true, "I want to order food, but I can’t eat shrimp", true, 1f, {}, {}) },
            "summary" to { s.lessonSummary(true, TeninIcon.UTENSILS, "Ordering food at a restaurant", "No shrimp: allergy", "Ordering, and asking about shrimp", {}, {}, {}, {}) },
            "reading" to { s.photoProgress(true, null, reading + TeninStep("Building your lesson…", false), false, "", {}, {}, {}, {}) },
            "ready" to { s.photoProgress(true, null, reading + TeninStep(s.emphasised("Lesson ready: ", "Ordering food"), true), true, "", {}, {}, {}, {}) },
            "glasses" to { s.putOnGlasses(true, "Ordering food", "Battery 18%. At 15% the phone stops powering the glasses and the lesson ends. Plug in power before you start.", {}, {}) },
            "stopped" to { s.lessonStopped("Your glasses disconnected", "Your phone’s battery is at 12%. Below 15% it stops powering the glasses. Plug in power, then start again.", {}, {}) },
            "running" to { s.running(true, "Ordering food", 2, 4, {}, {}) },
            "complete" to { s.complete("You ordered food and asked about shrimp, for real.", 3, 1, "lead with the allergy, then point at the dish.", {}, {}) },
            "review" to { s.review(3, sessions, "", {}, {}, nav(TeninUi.Tab.REVIEW)) },
            "session" to { s.sessionDetail(sampleSession.copy(phrases = reviewPhrases), {}, {}, {}, nav(TeninUi.Tab.REVIEW)) },
            "vocabularies" to { s.vocabularies(wordRows, TeninVocabularyTab.WORDS, lessons, "", "", "", {}, {}, {}, nav(TeninUi.Tab.VOCABULARIES)) },
            "vocabulary-phrases" to { s.vocabularies(phraseRows, TeninVocabularyTab.PHRASES, lessons, "", "", "", {}, {}, {}, nav(TeninUi.Tab.VOCABULARIES)) },
            "phrase" to { s.phraseDetail(phrases.first().copy(lesson = "Ordering food · used for real today"), {}, {}, { _, _ -> }, {}, {}, nav(TeninUi.Tab.VOCABULARIES)) },
        )
        val metrics = context.resources.displayMetrics
        val width = metrics.widthPixels
        // Leave out the status and navigation bars the activity pads for.
        val height = metrics.heightPixels - context.dp(24 + 16)
        val folder = File(app.filesDir, "tenin").apply { mkdirs() }
        for ((name, build) in screens) try {
            val view = build()
            view.measure(View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY), View.MeasureSpec.makeMeasureSpec(height, View.MeasureSpec.EXACTLY))
            view.layout(0, 0, width, height)
            val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
            view.draw(Canvas(bitmap))
            File(folder, "$name.png").outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
            bitmap.recycle()
        } catch (e: Exception) { Log.e("TeninSnapshot", name, e) }
        Log.i("TeninSnapshot", "wrote ${screens.size} screens to $folder")
    }
}
