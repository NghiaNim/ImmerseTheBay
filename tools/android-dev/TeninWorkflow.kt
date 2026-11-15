package com.immersethebay.phone

import android.Manifest
import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.media.MediaRecorder
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import java.io.File
import java.net.HttpURLConnection
import java.util.concurrent.CancellationException
import java.util.concurrent.Executors

/**
 * Drives the Tenin Live Lesson screens with the phone workflow: camera or library photo → photo practice,
 * spoken goal → written lesson (optionally grounded with a photo afterwards), and the glasses hand-off.
 */
object TeninWorkflow {
    private const val CAMERA = 31
    private const val PICK = 32
    private const val CAMERA_PERMISSION = 33
    private const val MIC_PERMISSION = 34
    private const val GRANTS = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
    private const val LEVEL = "N5"

    private val worker = Executors.newSingleThreadExecutor()
    private val disconnectWorker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private var owner: TeninActivity? = null
    private var expectedResult: Int? = null
    private var expectedPermission: Int? = null
    private var spokenGoal: String? = null
    private var goalDraft = ""
    private var goalLanguage = "en-US"
    private var recognizer: SpeechRecognizer? = null
    private var recognizerDeadline: Runnable? = null
    private var portalRecordingAfterPermission = false
    // Photo taken first: the next spoken goal is sent together with it.
    private class PreparedPhoto(val jpeg: ByteArray, val preview: Bitmap)
    private var pendingPhoto: PreparedPhoto? = null
    private var recorder: MediaRecorder? = null
    private var recording: File? = null
    private val requestLock = Any()
    private var activeRequest: HttpURLConnection? = null
    @Volatile private var generation = 0

    fun install() {
        // Each Live Lesson action starts fresh; only "Add a photo" carries a spoken goal over.
        TeninHooks.liveLesson = { activity, kind -> start(activity, kind) }
        TeninHooks.openGlasses = { activity ->
            // The glasses start the selected lesson themselves; no second tap on the XREAL controller.
            PracticePortal.requestLaunch()
            try { activity.startActivity(Intent().setClassName(activity.packageName, "ai.nreal.activitylife.NRXRActivity")); true }
            catch (_: ActivityNotFoundException) { false }
        }
        TeninHooks.endLesson = { PracticePortal.requestEnd() }
    }

    private fun start(activity: TeninActivity, kind: String, goal: String? = null) {
        owner?.let(::cancel)
        owner = activity
        spokenGoal = goal
        when (kind) {
            "photo" -> capture(activity)
            "library" -> launchPhotoIntent(activity, Intent(Intent.ACTION_GET_CONTENT).setType("image/*"), PICK)
            "voice" -> showGoalDraft(activity)
        }
    }

    // --- photo ------------------------------------------------------------------------------------

    private fun cameraFile(activity: Activity) = File(activity.cacheDir, "practice-capture.jpg")
    private fun cameraUri(activity: Activity) = Uri.parse("content://${activity.packageName}.practice-camera/capture.jpg")

    private fun capture(activity: TeninActivity) {
        if (activity.checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            expectedPermission = CAMERA_PERMISSION
            activity.requestPermissions(arrayOf(Manifest.permission.CAMERA), CAMERA_PERMISSION)
            return
        }
        cameraFile(activity).delete()
        launchPhotoIntent(activity, Intent(MediaStore.ACTION_IMAGE_CAPTURE).apply {
            putExtra(MediaStore.EXTRA_OUTPUT, cameraUri(activity))
            clipData = ClipData.newRawUri("Scene photo", cameraUri(activity))
            addFlags(GRANTS)
        }, CAMERA)
    }

    private fun launchPhotoIntent(activity: TeninActivity, intent: Intent, code: Int) {
        expectedResult = code
        try {
            activity.startActivityForResult(intent, code)
        } catch (_: ActivityNotFoundException) {
            expectedResult = null
            val other = if (code == CAMERA) "library" else "photo"
            val message = if (code == CAMERA) "No camera app is available." else "No image picker is available."
            failed(activity, message) { start(activity, other, spokenGoal) }
        }
    }

    fun onActivityResult(activity: TeninActivity, code: Int, result: Int, data: Intent?) {
        if (activity !== owner || code != expectedResult) return
        expectedResult = null
        if (code == CAMERA) activity.revokeUriPermission(cameraUri(activity), GRANTS)
        if (result != Activity.RESULT_OK) {
            if (code == CAMERA) cameraFile(activity).delete()
            return
        }
        val picked = data?.data
        if (code == PICK && picked == null) return
        activity.showPhotoProgress(null, listOf(TeninStep("Preparing your photo…", false)), ready = false,
            onContinue = {}, onRetake = {})
        val run = ++generation
        val source = if (code == CAMERA) runCatching {
            // Detach this capture from the fixed path used by the next camera intent.
            File.createTempFile("practice-capture-", ".jpg", activity.cacheDir).also { file ->
                if (!cameraFile(activity).renameTo(file)) {
                    file.delete()
                    throw IllegalStateException("Could not store captured photo")
                }
            }
        }.getOrNull() else null
        worker.execute {
            val prepared = runCatching {
                val image = if (code == CAMERA) try { PracticePortal.encodePhoto(requireNotNull(source)) } finally { source?.delete() }
                    else PracticePortal.encodePhoto(activity, picked!!)
                PreparedPhoto(image, requireNotNull(BitmapFactory.decodeByteArray(image, 0, image.size)))
            }.getOrNull()
            ui(activity, run) {
                when {
                    prepared == null -> failed(activity, "Could not read that photo. Try another one.") { start(activity, "photo", spokenGoal) }
                    // Goal already said (Add a photo from the lesson summary): build right away.
                    spokenGoal != null -> buildFromPhoto(activity, prepared, spokenGoal!!, run)
                    // Otherwise ask what they need to do here, then send the photo with that goal.
                    else -> { pendingPhoto = prepared; showGoalDraft(activity) }
                }
            }
        }
    }

    private fun buildFromPhoto(activity: TeninActivity, photo: PreparedPhoto, goal: String, run: Int) {
        val partnerVoice = activity.partnerVoicePreference
        val steps = listOf(TeninStep("Photo ready", true), TeninStep("Goal: $goal", true))
        activity.showPhotoProgress(photo.preview, steps + TeninStep("Building your lesson…", false), ready = false,
            onContinue = {}, onRetake = {})
        worker.execute {
            val made = runCatching { PracticePortal.createPractice(activity, photo.jpeg, goal, partnerVoice, requestTracker(run)) }
            ui(activity, run) {
                made.onSuccess { practice ->
                    pendingPhoto = null
                    PracticePortal.remember(practice)
                    activity.showPhotoProgress(photo.preview, steps + TeninStep(englishTitle(practice.title).let { "Lesson ready: $it" }, true), ready = true,
                        onContinue = { activity.showPutOnGlasses(practice.id, "", practice.title, photo.preview, practice.partnerVoice) },
                        onRetake = { start(activity, "photo", goal) }, update = true)
                }.onFailure {
                    // Try again: same photo and goal. Retake: a new photo.
                    activity.showPhotoProgress(photo.preview, steps, ready = false, error = it.message ?: "Could not build a lesson from that photo.",
                        onContinue = { buildFromPhoto(activity, photo, goal, ++generation) },
                        onRetake = { start(activity, "photo", goal) }, update = true)
                }
            }
        }
    }

    // --- spoken goal --------------------------------------------------------------------------------

    private fun showGoalDraft(activity: TeninActivity, status: String = "") {
        val run = generation
        activity.showGoalDraft(
            text = { goalDraft },
            language = goalLanguage,
            status = status,
            photo = pendingPhoto?.preview,
            onEdit = { if (owner === activity && run == generation) goalDraft = it },
            onLanguage = {
                if (owner === activity && run == generation) {
                    goalLanguage = it
                    showGoalDraft(activity, status)
                }
            },
            onDictate = { if (owner === activity && run == generation) listen(activity) },
            onRecord = { if (owner === activity && run == generation) listen(activity, portal = true) },
            onContinue = {
                if (owner === activity && run == generation) {
                    val goal = goalDraft.trim()
                    val photo = pendingPhoto
                    if (goal.length >= if (photo == null) 3 else 1) {
                        val next = ++generation
                        if (photo == null) writeLesson(activity, goal, next) else buildFromPhoto(activity, photo, goal, next)
                    }
                }
            },
        )
    }

    private fun listen(activity: TeninActivity, portal: Boolean = false) {
        if (recognizer != null || recorder != null || expectedPermission != null) return
        if (activity.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            portalRecordingAfterPermission = portal
            expectedPermission = MIC_PERMISSION
            activity.requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), MIC_PERMISSION)
            return
        }
        if (portal) recordForPortal(activity) else dictate(activity)
    }

    private fun dictate(activity: TeninActivity) {
        val next = runCatching {
            check(SpeechRecognizer.isRecognitionAvailable(activity))
            SpeechRecognizer.createSpeechRecognizer(activity)
        }.getOrElse {
            showGoalDraft(activity, "Live dictation is unavailable. Type your goal or try a recording.")
            return
        }
        val run = ++generation
        recognizer = next
        var partial = ""
        var finishing = false
        fun isCurrent() = owner === activity && run == generation && recognizer === next && !activity.isDestroyed
        fun result(bundle: Bundle?) = bundle?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)
            ?.firstOrNull()?.trim()?.take(500).orEmpty()
        fun complete(text: String, status: String = "") {
            if (!isCurrent()) return
            releaseRecognizer()
            val heard = text.ifBlank { partial }
            if (heard.isNotBlank()) goalDraft = heard
            showGoalDraft(activity, status)
        }
        fun schedule(delay: Long, action: () -> Unit) {
            recognizerDeadline?.let(main::removeCallbacks)
            recognizerDeadline = Runnable { if (isCurrent()) action() }.also { main.postDelayed(it, delay) }
        }
        fun finish() {
            if (!isCurrent() || finishing) return
            finishing = true
            activity.showSayGoal(partial, false, 0f, onMic = {}, update = true, busy = true, photo = pendingPhoto?.preview)
            schedule(4000) { complete(partial, "Review the words that were captured, or try again.") }
        }
        try {
            next.setRecognitionListener(object : RecognitionListener {
                override fun onReadyForSpeech(params: Bundle?) = Unit
                override fun onBeginningOfSpeech() = Unit
                override fun onBufferReceived(buffer: ByteArray?) = Unit
                override fun onEvent(eventType: Int, params: Bundle?) = Unit
                override fun onEndOfSpeech() = finish()
                override fun onRmsChanged(rmsdB: Float) {
                    if (isCurrent() && !finishing) activity.updateGoalLevel(((rmsdB + 2f) / 14f).coerceIn(0f, 1f))
                }
                override fun onPartialResults(partialResults: Bundle?) {
                    if (!isCurrent()) return
                    val text = result(partialResults)
                    if (text.isNotBlank() && text != partial) {
                        partial = text
                        goalDraft = text
                        activity.updateGoalTranscript(text, !finishing)
                    }
                }
                override fun onResults(results: Bundle?) {
                    val text = result(results)
                    complete(text, if (text.isBlank()) "Review what was captured, or try again." else "")
                }
                override fun onError(error: Int) {
                    val message = when (error) {
                        SpeechRecognizer.ERROR_NO_MATCH, SpeechRecognizer.ERROR_SPEECH_TIMEOUT ->
                            "No clear speech was heard. You can type your goal or try again."
                        SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS ->
                            "Allow microphone access in Settings, or type your goal."
                        else -> "Live dictation stopped. Edit what was captured or try a recording."
                    }
                    complete(partial, message)
                }
            })
            activity.showSayGoal("", true, 0f, onMic = {
                if (isCurrent() && !finishing) {
                    finish()
                    if (runCatching { next.stopListening() }.isFailure)
                        complete(partial, "Review what was captured, or try again.")
                }
            }, photo = pendingPhoto?.preview)
            schedule(27000) { finish(); runCatching { next.stopListening() } }
            next.startListening(Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
                putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                putExtra(RecognizerIntent.EXTRA_LANGUAGE, goalLanguage)
                putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
            })
        } catch (_: RuntimeException) {
            complete(partial, "Could not start live dictation. Type your goal or try a recording.")
        }
    }

    private fun releaseRecognizer() {
        recognizerDeadline?.let(main::removeCallbacks)
        recognizerDeadline = null
        val current = recognizer ?: return
        recognizer = null
        runCatching { current.cancel() }
        runCatching { current.destroy() }
    }

    private fun recordForPortal(activity: TeninActivity) {
        val file = runCatching { File.createTempFile("practice-goal-", ".m4a", activity.cacheDir) }.getOrElse {
            showGoalDraft(activity, "Could not prepare a recording.")
            return
        }
        @Suppress("DEPRECATION")
        val next = if (Build.VERSION.SDK_INT >= 31) MediaRecorder(activity) else MediaRecorder()
        try {
            next.setAudioSource(MediaRecorder.AudioSource.MIC)
            next.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
            next.setAudioEncoder(MediaRecorder.AudioEncoder.AAC)
            next.setAudioChannels(1)
            next.setAudioSamplingRate(16000)
            next.setAudioEncodingBitRate(64000)
            next.setMaxDuration(30000)
            next.setOnInfoListener { source, what, _ ->
                if (recorder === source && what == MediaRecorder.MEDIA_RECORDER_INFO_MAX_DURATION_REACHED) finishListening(activity)
            }
            next.setOutputFile(file.path)
            next.prepare()
            next.start()
        } catch (_: Exception) {
            next.release()
            file.delete()
            showGoalDraft(activity, "Could not start the microphone.")
            return
        }
        recorder = next
        recording = file
        val run = ++generation
        activity.showSayGoal("", true, 0f, onMic = { finishListening(activity) }, photo = pendingPhoto?.preview, portalRecording = true)
        // Live level for the waveform; tapping the mic again finishes.
        fun meter() {
            val current = recorder ?: return
            if (run != generation || activity.isDestroyed) return
            val level = (current.maxAmplitude / 12000f).coerceIn(0f, 1f)
            activity.updateGoalLevel(level)
            main.postDelayed({ meter() }, 250)
        }
        main.postDelayed({ meter() }, 250)
    }

    private fun finishListening(activity: TeninActivity) {
        if (recorder == null) return
        val file = recording
        recording = null
        val ok = stopRecorder()
        if (!ok || file == null || file.length() == 0L) {
            file?.delete()
            showGoalDraft(activity, "Nothing was recorded. Try again or type your goal.")
            return
        }
        val run = ++generation
        val language = goalLanguage
        activity.showSayGoal("", false, 0f, onMic = {}, update = true, busy = true, photo = pendingPhoto?.preview, portalRecording = true)
        worker.execute {
            val heard = runCatching {
                try { PracticePortal.transcribe(activity, file.readBytes(), language, requestTracker(run)) } finally { file.delete() }
            }
            ui(activity, run) {
                heard.onSuccess { text ->
                    goalDraft = text
                    showGoalDraft(activity)
                }
                    .onFailure { showGoalDraft(activity, it.message ?: "Could not understand that.") }
            }
        }
    }

    private fun writeLesson(activity: TeninActivity, goal: String, run: Int) {
        val partnerVoice = activity.partnerVoicePreference
        activity.showSayGoal(goal, false, 0f, onMic = {}, update = true, busy = true)
        worker.execute {
            val written = runCatching { PracticePortal.createScenario(activity, goal, LEVEL, "", partnerVoice, requestTracker(run)) }
            ui(activity, run) {
                written.onSuccess { lesson ->
                    val title = lesson.getString("title")
                    val role = lesson.optJSONObject("partnerRole")?.optString("en").orEmpty()
                    val first = lesson.optJSONArray("beats")?.optJSONObject(0)?.optJSONArray("lines")?.optJSONObject(0)
                    activity.showLessonSummary(iconFor(title + " " + goal), englishTitle(title),
                        if (role.isNotEmpty()) "You talk with the $role" else "",
                        first?.let { "${it.optString("jp")} · ${it.optString("en")}" }.orEmpty(),
                        // Adding a photo grounds the same goal in the real place (photo practice).
                        onAddPhoto = { start(activity, "photo", goal) },
                        onContinue = { activity.showPutOnGlasses(lesson.getString("id"), lesson.optString("startImage"), title, partnerVoice = lesson.optString("partnerVoice")) },
                        onAgain = { showGoalDraft(activity) })
                }.onFailure { showGoalDraft(activity, it.message ?: "Could not write a lesson from that.") }
            }
        }
    }

    // --- shared -------------------------------------------------------------------------------------

    fun onPermission(activity: TeninActivity, code: Int, grants: IntArray) {
        if (activity !== owner || code != expectedPermission) return
        expectedPermission = null
        val granted = grants.firstOrNull() == PackageManager.PERMISSION_GRANTED
        when (code) {
            CAMERA_PERMISSION -> if (granted) capture(activity) else failed(activity, "Allow the camera to take a photo, or choose one from your library.") { start(activity, "library", spokenGoal) }
            MIC_PERMISSION -> {
                val portal = portalRecordingAfterPermission
                portalRecordingAfterPermission = false
                if (granted) listen(activity, portal) else showGoalDraft(activity, "Allow microphone access, or type your goal.")
            }
        }
    }

    /** Release the microphone in the background, allowing a fresh take when the user returns. */
    fun pause(activity: TeninActivity) {
        if (activity !== owner || (recorder == null && recognizer == null)) return
        generation++
        releaseRecognizer()
        discardRecording()
        showGoalDraft(activity, "Recording stopped while the app was in the background.")
    }

    /** Cancels one preparation when its screen or owning activity goes away. */
    fun cancel(activity: TeninActivity) {
        if (activity !== owner) return
        owner = null
        if (expectedResult == CAMERA) {
            activity.revokeUriPermission(cameraUri(activity), GRANTS)
            cameraFile(activity).delete()
        }
        expectedResult = null
        expectedPermission = null
        portalRecordingAfterPermission = false
        spokenGoal = null
        goalDraft = ""
        pendingPhoto = null
        val request = synchronized(requestLock) {
            generation++
            activeRequest.also { activeRequest = null }
        }
        if (request != null) disconnectWorker.execute { request.disconnect() }
        releaseRecognizer()
        discardRecording()
    }

    private fun requestTracker(run: Int): (HttpURLConnection?) -> Unit = { request ->
        val current = synchronized(requestLock) {
            (run == generation).also { if (it) activeRequest = request }
        }
        if (!current && request != null) {
            request.disconnect()
            throw CancellationException("Preparation was canceled")
        }
    }

    private fun discardRecording() {
        stopRecorder()
        recording?.delete()
        recording = null
    }

    private fun stopRecorder(): Boolean {
        val current = recorder ?: return false
        recorder = null
        // stop() throws when almost nothing was recorded.
        return try { current.stop(); true } catch (_: RuntimeException) { false } finally { current.release() }
    }

    private fun failed(activity: TeninActivity, message: String, retry: () -> Unit) =
        activity.showPhotoProgress(pendingPhoto?.preview, emptyList(), ready = false, error = message, onContinue = retry, onRetake = retry)

    private fun ui(activity: TeninActivity, run: Int, block: () -> Unit) =
        main.post { if (activity === owner && run == generation && !activity.isDestroyed) block() }

    private fun englishTitle(title: String) = title.substringAfter(" — ", title).trim()

    private fun iconFor(text: String) = when {
        Regex("restaurant|food|cafe|café|coffee|order|menu|餐|咖啡|食", RegexOption.IGNORE_CASE).containsMatchIn(text) -> TeninIcon.UTENSILS
        Regex("shop|store|market|buy|supermarket|买|店", RegexOption.IGNORE_CASE).containsMatchIn(text) -> TeninIcon.CART
        Regex("doctor|clinic|hospital|pharmacy|医", RegexOption.IGNORE_CASE).containsMatchIn(text) -> TeninIcon.STETHOSCOPE
        Regex("home|house|家", RegexOption.IGNORE_CASE).containsMatchIn(text) -> TeninIcon.HOME
        else -> TeninIcon.SPARKLE
    }
}
