package com.immersethebay.phone

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.hardware.display.DisplayManager
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.tts.TextToSpeech
import android.util.LruCache
import android.view.Display
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.widget.FrameLayout
import android.widget.ImageView
import org.json.JSONObject
import java.lang.ref.WeakReference
import java.net.HttpURLConnection
import java.net.URL
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import java.util.concurrent.Executors

/**
 * Seams between the Tenin screens and the lesson workflow. Every hook is optional: without it the
 * screens stay navigable, and the matching button is hidden or explains what is missing.
 */
object TeninHooks {
    /** Lesson the glasses should start: id, start image path and title (same shape Unity already reads). */
    @Volatile var selected: Array<String> = emptyArray()
    /** Opens the glasses view for [selected]. Default: start the XREAL activity bundled with the app. */
    @Volatile var openGlasses: ((Activity) -> Boolean)? = null
    /** Stops the lesson running in the glasses. */
    @Volatile var endLesson: ((Activity) -> Unit)? = null
    /** Pauses or resumes the lesson. The Pause button is hidden while this is null. */
    @Volatile var pauseLesson: ((Activity) -> Unit)? = null
    /** Live Lesson entry points: "photo", "library" or "voice". Drive the result with TeninActivity.show… */
    @Volatile var liveLesson: ((TeninActivity, String) -> Unit)? = null
    /** Current step and step count while a lesson runs, or null when unknown. */
    @Volatile var progress: (() -> Pair<Int, Int>?)? = null
}

/** Tenin phone UI: Start, Pre-made lessons, Review and Vocabularies. Live Lesson screens are driven through TeninHooks. */
class TeninActivity : Activity() {
    companion object {
        /** For Unity: the lesson chosen on the phone, or an empty array. */
        @JvmStatic fun selectedSession(): Array<String> = TeninHooks.selected
    }

    private lateinit var screens: TeninScreens
    private lateinit var root: FrameLayout
    private val stack = ArrayList<() -> View>()
    private var preparationDepth = 0
    private val worker = Executors.newSingleThreadExecutor()
    private val previewWorker = Executors.newSingleThreadExecutor()
    private val previewCache = object : LruCache<String, Bitmap>(12 * 1024 * 1024) {
        override fun sizeOf(key: String, value: Bitmap) = value.allocationByteCount
    }
    private val pendingPreviews = mutableMapOf<String, MutableList<WeakReference<ImageView>>>()
    private val unavailablePreviews = mutableSetOf<String>()
    private val bundledPreviewPath = Regex("lessons/images/[A-Za-z0-9][A-Za-z0-9._-]{0,99}")

    private val main = Handler(Looper.getMainLooper())
    private var demo = false
    private var tts: TextToSpeech? = null
    private var ttsReady = false
    private var scenarios: List<TeninScenario> = emptyList()
    private var chosen = 0
    private var sessions: List<TeninSession> = emptyList()
    private var sessionStatus = ""
    private var loadedAt = 0L
    private var filter = ""
    private var query = ""
    private var running: TeninScenario? = null
    // A lesson was handed to the glasses and has not come back through End lesson yet.
    private var inGlasses = false
    private var glassesLost = false
    @Volatile private var closed = false

    private val displays: DisplayManager get() = getSystemService(Context.DISPLAY_SERVICE) as DisplayManager
    private val connected: Boolean
        get() = demo || displays.displays.any { it.displayId != Display.DEFAULT_DISPLAY }
    // Keeps the glasses status current as they are plugged in or out.
    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) = render()
        override fun onDisplayRemoved(displayId: Int) { if (inGlasses) glassesLost = true; render() }
        override fun onDisplayChanged(displayId: Int) {}
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        displays.registerDisplayListener(displayListener, main)
        demo = intent.getBooleanExtra("demo", false)
        if (!demo) TeninWorkflow.install()
        screens = TeninScreens(this, ::bindScenarioPreview)
        root = FrameLayout(this).apply { setBackgroundColor(Tenin.BG) }
        root.setOnApplyWindowInsetsListener { view, insets ->
            if (Build.VERSION.SDK_INT >= 30) {
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime())
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            } else @Suppress("DEPRECATION") view.setPadding(insets.systemWindowInsetLeft, insets.systemWindowInsetTop, insets.systemWindowInsetRight, insets.systemWindowInsetBottom)
            insets
        }
        setContentView(root)
        window.statusBarColor = Tenin.BG
        window.navigationBarColor = Tenin.BG
        window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR or View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR
        scenarios = if (demo) TeninDemo.scenarios else loadScenarios()
        if (demo) sessions = TeninDemo.sessions
        reset(::startScreen)
        // `adb shell am start … --es screen <name>` opens one screen directly for design review.
        intent.getStringExtra("screen")?.let(::openForReview)
        if (!demo) consumeCompletion(intent)
    }

    // --- navigation -------------------------------------------------------------------------

    private fun render() {
        if (closed || stack.isEmpty()) return
        root.removeAllViews()
        root.addView(stack[stack.size - 1].invoke(), ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
    }
    private fun go(screen: () -> View) { stack.add(screen); render() }
    private fun replace(screen: () -> View) { if (stack.isNotEmpty()) stack.removeAt(stack.size - 1); go(screen) }
    private fun reset(screen: () -> View) {
        if (!demo) TeninWorkflow.cancel(this)
        preparationDepth = 0
        stack.clear()
        go(screen)
    }

    private fun back() {
        if (!demo && (preparationDepth == 0 || preparationDepth == stack.size)) {
            TeninWorkflow.cancel(this)
            preparationDepth = 0
        }
        if (stack.size > 1) { stack.removeAt(stack.size - 1); render() } else finish()
    }

    private fun showPreparation(screen: () -> View, update: Boolean = false) {
        if (demo) {
            if (update) replace(screen) else go(screen)
        } else if (preparationDepth > 0 && preparationDepth == stack.size) {
            replace(screen)
        } else {
            preparationDepth = stack.size + 1
            go(screen)
        }
    }
    @Deprecated("Handles the in-app screen stack.") override fun onBackPressed() = back()

    private fun nav(active: TeninUi.Tab) = screens.ui.bottomNav(active) { tab ->
        when (tab) {
            TeninUi.Tab.START -> reset(::startScreen)
            TeninUi.Tab.VOCABULARIES -> { reset(::vocabularyScreen); refreshSessions() }
            TeninUi.Tab.REVIEW -> { reset(::reviewScreen); refreshSessions() }
        }
    }

    // --- Start and pre-made lessons ------------------------------------------------------------

    private fun startScreen(): View = screens.start(connected,
        onLive = { go(::liveScreen) }, onPremade = { go(::chooseScreen) }, nav = nav(TeninUi.Tab.START))

    private fun chooseScreen(): View = screens.chooseScenario(connected, scenarios, chosen,
        onSelect = { chosen = it; render() }, onOpen = { chosen = it; go(::detailScreen) },
        onStart = { prepare(scenarios[chosen]) }, onBack = ::back, nav = nav(TeninUi.Tab.START))

    private fun bindScenarioPreview(path: String, view: ImageView) {
        if (!path.matches(bundledPreviewPath) || path in unavailablePreviews) return
        previewCache.get(path)?.let { view.setImageBitmap(it); return }
        val waiters = pendingPreviews[path]
        if (waiters != null) {
            waiters.removeAll { it.get() == null }
            waiters.add(WeakReference(view))
            return
        }
        pendingPreviews[path] = mutableListOf(WeakReference(view))
        previewWorker.execute {
            val bitmap = runCatching { decodeBundledPreview(path) }.getOrNull()
            main.post {
                if (closed) return@post
                val targets = pendingPreviews.remove(path).orEmpty()
                if (bitmap == null) { unavailablePreviews.add(path); return@post }
                previewCache.put(path, bitmap)
                targets.forEach { it.get()?.setImageBitmap(bitmap) }
            }
        }
    }

    private fun decodeBundledPreview(path: String): Bitmap? {
        val options = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        assets.open(path).use { BitmapFactory.decodeStream(it, null, options) }
        val width = options.outWidth
        val height = options.outHeight
        if (width <= 0 || height <= 0 || width.toLong() * height > 80_000_000L) return null
        var sample = 1
        while (maxOf(width, height) / sample > 1280) sample *= 2
        options.inJustDecodeBounds = false
        options.inSampleSize = sample
        return assets.open(path).use { BitmapFactory.decodeStream(it, null, options) }
    }

    private fun detailScreen(): View = screens.scenarioDetail(connected, scenarios[chosen], onSpeak = { speak(it.jp) },
        onStart = { prepare(scenarios[chosen]) }, onBack = ::back)

    /** Hands a lesson to the glasses: remember the choice, then ask the wearer to put them on. */
    private fun prepare(scenario: TeninScenario, photo: Bitmap? = null) {
        TeninHooks.selected = arrayOf(scenario.id, scenario.startImage, scenario.title)
        running = scenario
        go { screens.putOnGlasses(connected, scenario.title, batteryWarning(), onWearing = ::startInGlasses, onBack = ::back, photo = photo, partnerVoice = scenario.partnerVoice) }
    }

    private fun battery(): Pair<Int, Boolean> {
        val manager = getSystemService(Context.BATTERY_SERVICE) as BatteryManager
        return manager.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY) to manager.isCharging
    }

    // The phone powers the glasses. On the S25 that power is cut at 15% battery, which ends the lesson.
    private fun batteryWarning(): String {
        val (level, charging) = battery()
        return when {
            demo || charging || level > 20 -> ""
            level <= 15 -> "Battery $level%. Below 15% the phone keeps cutting power to the glasses, so the lesson will stop. Plug in power first."
            else -> "Battery $level%. At 15% the phone stops powering the glasses and the lesson ends. Plug in power before you start."
        }
    }

    private fun startInGlasses() {
        val scenario = running ?: return
        val opened = demo || (TeninHooks.openGlasses ?: { activity ->
            try { activity.startActivity(Intent().setClassName(activity.packageName, "ai.nreal.activitylife.NRXRActivity")); true }
            catch (_: Exception) { false }
        })(this)
        if (!opened) return
        inGlasses = !demo
        glassesLost = false
        replace {
            val progress = if (demo) 2 to 4 else TeninHooks.progress?.invoke()
            screens.running(connected, scenario.title, progress?.first ?: 0, progress?.second ?: 0,
                onPause = TeninHooks.pauseLesson?.let { hook -> { hook(this) } }, onEnd = { endLesson() })
        }
    }

    // Own the glasses display while the phone UI is in front (instead of Samsung's stretched mirror).
    override fun onResume() {
        super.onResume()
        if (demo) return
        TeninGlassesScreen.show(this)
        // Back without End lesson (glasses disconnected, Back to Tenin, Home): the lesson has stopped there.
        if (inGlasses) { inGlasses = false; running?.let(::showStopped) }
    }
    override fun onPause() {
        if (!demo) {
            TeninWorkflow.pause(this)
            TeninGlassesScreen.hide(this)
        }
        super.onPause()
    }

    /** Back from the glasses (their End lesson button): show the summary without stopping them again. */
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        if (!demo) consumeCompletion(intent)
    }

    private fun consumeCompletion(intent: Intent) {
        val runId = intent.getStringExtra("completedRunId") ?: return
        intent.removeExtra("completedRunId")
        if (!runId.matches(runIdPattern)) return
        inGlasses = false
        running = null
        loadCompletion(runId)
    }

    private fun showStopped(scenario: TeninScenario) {
        val lost = glassesLost || !connected
        val (level, charging) = battery()
        val detail = when {
            lost && !charging && level <= 15 -> "Your phone’s battery is at $level%. Below 15% it stops powering the glasses. Plug in power, then start again."
            lost -> "Check that the glasses are plugged in, then start again."
            else -> "The lesson stops when you leave the glasses view. Start again when you’re ready."
        }
        reset { screens.lessonStopped(if (lost) "Your glasses disconnected" else "Lesson stopped", detail,
            onAgain = { prepare(scenario) }, onDone = { running = null; reset(::startScreen) }) }
    }

    private val runIdPattern = Regex("[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}")

    private fun endLesson() {
        if (demo) {
            running = null
            showComplete("You ordered food and asked about shrimp, for real.", 3, 1, "lead with the allergy, then point at the dish.")
        } else {
            TeninHooks.endLesson?.invoke(this)
        }
    }

    private fun loadCompletion(runId: String) {
        val goHome: () -> Unit = { reset(::startScreen) }
        val loading: () -> View = { screens.completionStatus(onDone = goHome) }
        reset(loading)
        worker.execute {
            val result = runCatching {
                val detail = request("/api/sessions/$runId")
                val record = detail.getJSONObject("record")
                check(record.getString("id") == runId) { "Unexpected session record" }
                parseSession(record, detail) to (record.optString("status") == "complete")
            }
            main.post {
                if (closed || stack.lastOrNull() !== loading) return@post
                result.onSuccess { (session, completed) ->
                    sessions = listOf(session) + sessions.filterNot { it.id == runId }
                    loadedAt = 0
                    showComplete(session.note, session.phrases.count { it.mine && it.passed },
                        session.phrases.count { it.mine && !it.passed }, session.tip, completed)
                }.onFailure {
                    reset { screens.completionStatus(onRetry = { loadCompletion(runId) }, onDone = goHome) }
                }
            }
        }
    }

    // --- Live Lesson: screens the photo and speech workflow drives -----------------------------

    private var liveNote = ""
    var partnerVoicePreference = "auto"
        private set
    private fun liveScreen(): View {
        fun start(kind: String) {
            val hook = TeninHooks.liveLesson
            if (hook != null) hook(this, kind) else if (demo) openForReview(if (kind == "voice") "goal" else "reading")
        }
        return screens.liveLesson(connected, onPhoto = { start("photo") }, onLibrary = { start("library") }, onVoice = { start("voice") },
            onBack = ::back, nav = nav(TeninUi.Tab.START), partnerVoice = partnerVoicePreference,
            onPartnerVoice = { partnerVoicePreference = it; render() })
    }

    private var visibleGoalTranscript = ""

    /** Listening screen for a spoken goal. Partial text and audio level update the mounted views. */
    fun showSayGoal(transcript: String, listening: Boolean, level: Float, onMic: () -> Unit, onClose: () -> Unit = ::back, update: Boolean = false, busy: Boolean = false, photo: Bitmap? = null, portalRecording: Boolean = false) {
        visibleGoalTranscript = transcript
        showPreparation({ screens.sayGoal(connected, visibleGoalTranscript, listening, level, onMic, onClose, busy, photo, portalRecording) }, update)
    }

    fun updateGoalLevel(level: Float) = screens.updateGoalLevel(root, level)
    fun updateGoalTranscript(transcript: String, listening: Boolean) {
        visibleGoalTranscript = transcript
        screens.updateGoalTranscript(root, transcript, listening)
    }

    fun showGoalDraft(text: () -> String, language: String, status: String, photo: Bitmap?, onEdit: (String) -> Unit,
                      onLanguage: (String) -> Unit, onDictate: () -> Unit, onRecord: () -> Unit, onContinue: () -> Unit) =
        showPreparation({
            screens.goalDraft(connected, text(), language, status, photo, onEdit, onLanguage, onDictate, onRecord, onContinue, ::back)
        })

    /** "Reading your photo" while steps are unfinished, "Photo ready" once `ready`, or an error with retry. */
    fun showPhotoProgress(photo: Bitmap?, steps: List<TeninStep>, ready: Boolean, error: String = "", onContinue: () -> Unit, onRetake: () -> Unit, onCancel: () -> Unit = ::back, update: Boolean = false) {
        showPreparation({ screens.photoProgress(connected, photo, steps, ready, error, onContinue, onRetake, onCancel, ::back) }, update)
    }

    /** "Here's your lesson" after a spoken goal. Empty `remember` or `practice` rows are left out. */
    fun showLessonSummary(icon: TeninIcon, situation: String, remember: String, practice: String, onAddPhoto: (() -> Unit)?, onContinue: () -> Unit, onAgain: () -> Unit) =
        showPreparation({ screens.lessonSummary(connected, icon, situation, remember, practice, onAddPhoto, onContinue, onAgain, ::back) })

    /** Continue a generated lesson into the glasses, exactly like a pre-made one. */
    fun showPutOnGlasses(lessonId: String, startImage: String, title: String, photo: Bitmap? = null, partnerVoice: String = "") =
        prepare(TeninScenario(lessonId, title, "", "", "", TeninIcon.CAMERA, emptyList(), startImage, partnerVoice = partnerVoice), photo)

    fun showComplete(summary: String, saved: Int, review: Int, tip: String, completed: Boolean = true) = reset {
        screens.complete(summary, saved, review, tip, onPhrases = { reset(::vocabularyScreen); refreshSessions() },
            onDone = { reset(::startScreen) }, completed = completed)
    }

    // --- Review and vocabularies -----------------------------------------------------------------

    private fun tricky(session: TeninSession) = session.phrases.count { it.mine && !(it.passed && it.tries <= 1) }

    private fun reviewScreen(): View {
        val due = sessions.sumOf(::tricky)
        return screens.review(due, sessions, if (sessions.isEmpty()) sessionStatus.ifEmpty { "Lessons you finish will appear here." } else "",
            onReview = { sessions.firstOrNull { tricky(it) > 0 }?.let { replay(it, "missed") } },
            onOpen = { session -> go { sessionScreen(session) } }, nav = nav(TeninUi.Tab.REVIEW))
    }

    private fun sessionScreen(session: TeninSession): View = screens.sessionDetail(session,
        onPracticeTricky = if (tricky(session) > 0) ({ replay(session, "missed") }) else null,
        onReplay = { replay(session, "all") }, onBack = ::back, nav = nav(TeninUi.Tab.REVIEW))

    private fun vocabulary() = sessions.flatMap { s -> s.phrases.filter { it.mine }.map { it.copy(lesson = s.title) } }.distinctBy { it.jp }

    private fun vocabularyScreen(): View {
        val all = vocabulary()
        val shown = all.filter { (filter.isEmpty() || it.lesson == filter) && (query.isBlank() || listOf(it.jp, it.romaji, it.en).any { field -> field.contains(query.trim(), ignoreCase = true) }) }
        return screens.vocabularies(shown, all.map { it.lesson }.distinct(), filter, query, if (all.isEmpty()) sessionStatus else if (shown.isEmpty()) "No phrases match." else "",
            onFilter = { filter = it; render() }, onQuery = { query = it; main.removeCallbacksAndMessages("query"); main.postAtTime({ render() }, "query", android.os.SystemClock.uptimeMillis() + 400) },
            onSpeak = { speak(it.jp) }, onOpen = { phrase -> go { phraseScreen(phrase) } }, nav = nav(TeninUi.Tab.VOCABULARIES))
    }

    private fun phraseScreen(phrase: TeninPhrase): View = screens.phraseDetail(phrase, onListen = { speak(phrase.jp) }, onSlowly = { speak(phrase.jp, 0.55f) },
        onPractice = sessions.firstOrNull { s -> s.phrases.any { it.jp == phrase.jp } }?.let { session -> { replay(session, "all") } },
        onBack = ::back, nav = nav(TeninUi.Tab.VOCABULARIES))

    /** Compiles a saved session into a lesson on the portal and hands it to the glasses. */
    private fun replay(session: TeninSession, deck: String) {
        if (demo) { prepare(TeninScenario(session.id, session.title, "", "", "", TeninIcon.GLASSES, emptyList())); return }
        worker.execute {
            val result = runCatching {
                val made = request("/api/sessions/${session.id}/replay", JSONObject().put("deck", deck))
                val lesson = request("/api/agent/lessons/${made.getString("lessonId")}")
                TeninScenario(lesson.getString("id"), session.title, "", "", "", TeninIcon.PLAY, emptyList(),
                    lesson.optString("startImage"), partnerVoice = lesson.optString("partnerVoice"))
            }
            main.post {
                if (closed) return@post
                result.onSuccess { prepare(it) }
                    .onFailure { sessionStatus = "Could not prepare that review. Check the connection and try again."; render() }
            }
        }
    }

    private fun refreshSessions(force: Boolean = false, then: (() -> Unit)? = null) {
        if (demo) { then?.invoke(); return }
        if (!force && System.currentTimeMillis() - loadedAt < 15_000) { then?.invoke(); return }
        if (sessions.isEmpty()) { sessionStatus = "Loading…"; render() }
        worker.execute {
            val result = runCatching {
                val list = request("/api/sessions").getJSONArray("sessions")
                (0 until minOf(list.length(), 12)).map { i ->
                    val item = list.getJSONObject(i)
                    parseSession(item, request("/api/sessions/${item.getString("id")}"))
                }
            }
            main.post {
                if (closed) return@post
                result.onSuccess { sessions = it; sessionStatus = ""; loadedAt = System.currentTimeMillis() }
                    .onFailure { sessionStatus = "Could not load your sessions. Check that the phone can reach the lesson service." }
                if (then != null) then() else render()
            }
        }
    }

    private fun parseSession(item: JSONObject, detail: JSONObject): TeninSession {
        val lines = detail.optJSONArray("lines")
        val phrases = (0 until (lines?.length() ?: 0)).mapNotNull { i ->
            val entry = lines!!.getJSONObject(i)
            val line = entry.optJSONObject("line") ?: return@mapNotNull null
            val tries = entry.optJSONArray("attempts")?.length() ?: 0
            if (tries == 0 && !entry.optBoolean("skipped")) null
            else readPhrase(line).copy(tries = tries, passed = entry.optBoolean("passed"),
                partner = line.optJSONObject("partner")?.let { readPhrase(it, mine = false) }?.takeIf { it.jp.isNotBlank() })
        }
        val passed = phrases.count { it.passed }
        return TeninSession(item.getString("id"), dayLabel(item.optString("startedAt")), item.optString("mode") == "live",
            englishTitle(item.optString("lessonTitle")), if (phrases.isEmpty()) "No phrases attempted." else "$passed of ${phrases.size} phrases understood.", phrases = phrases)
    }

    private fun dayLabel(iso: String): String = runCatching {
        val day = Instant.parse(iso).atZone(ZoneId.systemDefault()).toLocalDate()
        val today = LocalDate.now()
        when (day) { today -> "Today"; today.minusDays(1) -> "Yesterday"; else -> day.format(DateTimeFormatter.ofPattern("MMM d", Locale.US)) }
    }.getOrDefault("")

    // --- data -----------------------------------------------------------------------------------

    private fun englishTitle(title: String) = title.substringAfter(" — ", title).trim()

    private fun readPhrase(line: JSONObject, mine: Boolean = true) = TeninPhrase(
        line.optString("jp"),
        if (line.isNull("romaji")) "" else line.optString("romaji"),
        line.optString("en"),
        mine = mine,
    )

    /** Pre-made lessons are the lesson files bundled with the app. */
    private fun loadScenarios(): List<TeninScenario> = runCatching { assets.list("lessons").orEmpty().filter { it.endsWith(".json") }.sorted().mapNotNull { name ->
        runCatching {
            val lesson = JSONObject(assets.open("lessons/$name").bufferedReader().use { it.readText() })
            val beats = lesson.getJSONArray("beats")
            val goals = (0 until beats.length()).map { beats.getJSONObject(it).optString("goal") }.filter { it.isNotEmpty() }
            val first = beats.getJSONObject(0).getJSONArray("lines")
            val phrases = (0 until minOf(first.length(), 3)).flatMap { i ->
                val line = first.getJSONObject(i)
                listOfNotNull(line.optJSONObject("partner")?.let { readPhrase(it, mine = false) }?.takeIf { it.jp.isNotBlank() },
                    readPhrase(line))
            }.take(3)
            val id = lesson.getString("id")
            val title = lesson.getString("title")
            TeninScenario(id, englishTitle(title), title.substringBefore(" — ", "").trim(), goals.firstOrNull().orEmpty(), goals.take(2).joinToString(" "),
                when {
                    Regex("konbini|store|market|shop").containsMatchIn(id) -> TeninIcon.CART
                    Regex("home|house").containsMatchIn(id) -> TeninIcon.HOME
                    Regex("restaurant|food|cafe").containsMatchIn(id) -> TeninIcon.UTENSILS
                    Regex("doctor|clinic|hospital").containsMatchIn(id) -> TeninIcon.STETHOSCOPE
                    else -> TeninIcon.PLAY
                }, phrases, lesson.optString("startImage"), beats.length(),
                if (lesson.has("graph")) "Side trips can happen, like asking for something else." else "",
                partnerVoice = lesson.optString("partnerVoice"))
        }.getOrNull()
    }.let { catalog ->
        if (catalog.any { it.id == "ja-konbini-demo-001" }) catalog.filterNot { it.id == "ja-konbini-001" }
        else catalog
    } }.getOrDefault(emptyList())

    /** GET, or POST when `body` is given, against the portal named in the bundled visko.json. */
    private fun request(path: String, body: JSONObject? = null): JSONObject {
        val config = JSONObject(assets.open("visko.json").bufferedReader().use { it.readText() })
        val connection = URL(config.getString("portalUrl").trimEnd('/') + path).openConnection() as HttpURLConnection
        try {
            connection.connectTimeout = 8000
            connection.readTimeout = 20000
            config.optString("portalToken").takeIf { it.isNotBlank() }?.let { connection.setRequestProperty("portal_token", it) }
            if (body != null) {
                connection.requestMethod = "POST"
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json")
                connection.outputStream.use { it.write(body.toString().toByteArray()) }
            }
            if (connection.responseCode !in 200..299) throw IllegalStateException("HTTP ${connection.responseCode}")
            return JSONObject(connection.inputStream.bufferedReader().use { it.readText() })
        } finally { connection.disconnect() }
    }

    private fun speak(japanese: String, rate: Float = 0.95f) {
        fun say() { tts?.setSpeechRate(rate); tts?.speak(japanese, TextToSpeech.QUEUE_FLUSH, null, "tenin") }
        if (ttsReady) { say(); return }
        if (tts != null) return
        tts = TextToSpeech(applicationContext) { status ->
            if (status == TextToSpeech.SUCCESS && !closed) { tts?.language = Locale.JAPAN; ttsReady = true; say() }
        }
    }

    /** Opens one screen with sample content so each mockup can be compared on a device. */
    private fun openForReview(name: String) {
        val food = TeninDemo.scenarios.first()
        when (name) {
            "choose" -> go(::chooseScreen)
            "detail" -> go(::detailScreen)
            "live" -> go(::liveScreen)
            "goal" -> showSayGoal("I want to order food, but I can’t eat shrimp", true, 0.9f, onMic = { openForReview("summary") })
            "summary" -> showLessonSummary(TeninIcon.UTENSILS, "Ordering food at a restaurant", "No shrimp: allergy", "Ordering, and asking about shrimp", onAddPhoto = { openForReview("reading") }, onContinue = { prepare(food) }, onAgain = ::back)
            "reading" -> showPhotoProgress(null, listOf(TeninStep("Spotted a restaurant", true), TeninStep("Found the menu board", true), TeninStep("Building your lesson…", false)), false, onContinue = {}, onRetake = ::back, onCancel = { openForReview("ready") })
            "ready" -> showPhotoProgress(null, listOf(TeninStep("Spotted a restaurant", true), TeninStep("Found the menu board", true), TeninStep(screens.emphasised("Lesson ready: ", "Ordering food"), true)), true, onContinue = { prepare(food) }, onRetake = ::back)
            "glasses" -> prepare(scenarios.first())
            "running" -> { running = scenarios.first(); go { View(this) }; startInGlasses() }
            "complete" -> showComplete("You ordered food and asked about shrimp, for real.", 3, 1, "lead with the allergy, then point at the dish.")
            "review" -> reset(::reviewScreen)
            "session" -> sessions.firstOrNull()?.let { session -> go { sessionScreen(session) } }
            "vocabularies" -> reset(::vocabularyScreen)
            "phrase" -> vocabulary().firstOrNull()?.let { phrase -> go { phraseScreen(phrase) } }
        }
    }

    @Deprecated("Camera, photo picker and permission results for the Live Lesson workflow.")
    override fun onActivityResult(code: Int, result: Int, data: Intent?) {
        super.onActivityResult(code, result, data)
        if (!demo) TeninWorkflow.onActivityResult(this, code, result, data)
    }

    override fun onRequestPermissionsResult(code: Int, permissions: Array<String>, grants: IntArray) {
        super.onRequestPermissionsResult(code, permissions, grants)
        if (!demo) TeninWorkflow.onPermission(this, code, grants)
    }

    override fun onDestroy() {
        closed = true
        displays.unregisterDisplayListener(displayListener)
        if (!demo) TeninWorkflow.cancel(this)
        main.removeCallbacksAndMessages(null)
        tts?.shutdown()
        previewWorker.shutdownNow()
        pendingPreviews.clear()
        previewCache.evictAll()
        worker.shutdownNow()
        super.onDestroy()
    }
}

/** Sample content matching the mockups, shown with `--ez demo true`. Never used in a normal launch. */
object TeninDemo {
    private val takoyaki = TeninPhrase("たこ焼きをください。", "Takoyaki o kudasai", "Can I have the takoyaki?", lesson = "Ordering food · used for real today",
        words = listOf(Triple("たこ焼き", "takoyaki", "octopus balls"), Triple("を", "o", "marks what you want"), Triple("ください", "kudasai", "please give me")),
        swapTitle = "Swap the dish", swap = "すし / 天ぷら / お水 をください。", tries = 1)
    val scenarios = listOf(
        TeninScenario("demo-restaurant", "Ordering food", "レストラン", "Order a meal, ask about allergies", "Get seated, order a meal, and ask whether a dish has shrimp.", TeninIcon.UTENSILS,
            listOf(TeninPhrase("いらっしゃいませ。", "Irasshaimase", "Welcome!", mine = false), TeninPhrase("一人です。", "Hitori desu", "Just one, please."), takoyaki),
            steps = 4, note = "Side trips can happen, like a wrong order."),
        TeninScenario("demo-spill", "Spilled something", "こぼした！", "A small emergency at the table", "Tell the staff what happened and ask for help.", TeninIcon.CUP, emptyList()),
        TeninScenario("demo-supermarket", "Supermarket", "スーパー", "Find items and pay at the register", "Find what you need and pay.", TeninIcon.CART, emptyList()),
        TeninScenario("demo-doctor", "Talking to the doctor", "病院", "Describe how you feel", "Explain your symptoms.", TeninIcon.STETHOSCOPE, emptyList()),
    )
    val sessions = listOf(
        TeninSession("demo-1", "Today", true, "Ordering food", "You asked about your allergy, for real.", "Well done! You successfully asked about your allergy.", "lead with the allergy, then point at the dish.",
            listOf(takoyaki, TeninPhrase("えびアレルギーがあります。", "Ebi arerugī ga arimasu", "I’m allergic to shrimp", tries = 1),
                TeninPhrase("これにえびは入っていますか？", "Kore ni ebi wa haitte imasu ka", "Does this have shrimp?", tries = 2))),
        TeninSession("demo-2", "Yesterday", false, "Ordering food", "Retried once, then got it.", phrases = listOf(takoyaki.copy(tries = 2))),
        TeninSession("demo-3", "Nov 11", false, "Supermarket", "Handled the bag question.", phrases = listOf(TeninPhrase("袋はいりません。", "Fukuro wa irimasen", "I don’t need a bag", tries = 1))),
    )
}
