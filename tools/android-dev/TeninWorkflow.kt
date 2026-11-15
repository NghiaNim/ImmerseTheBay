package com.immersethebay.phone

import android.Manifest
import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import android.media.MediaRecorder
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import java.io.File
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
    private const val LEVEL = "N4"

    private val worker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private var spokenGoal: String? = null
    // Photo taken first: the next spoken goal is sent together with it.
    private var pendingPhoto: ByteArray? = null
    private var recorder: MediaRecorder? = null
    private var recording: File? = null
    private var generation = 0

    fun install() {
        // Each Live Lesson action starts fresh; only "Add a photo" carries a spoken goal over.
        TeninHooks.liveLesson = { activity, kind -> spokenGoal = null; start(activity, kind) }
        TeninHooks.openGlasses = { activity ->
            // The glasses start the selected lesson themselves; no second tap on the XREAL controller.
            PracticePortal.requestLaunch()
            try { activity.startActivity(Intent().setClassName(activity.packageName, "ai.nreal.activitylife.NRXRActivity")); true }
            catch (_: ActivityNotFoundException) { false }
        }
        TeninHooks.endLesson = { PracticePortal.requestEnd() }
    }

    private fun start(activity: TeninActivity, kind: String) {
        if (kind != "voice") pendingPhoto = null
        when (kind) {
            "photo" -> capture(activity)
            "library" -> activity.startActivityForResult(Intent(Intent.ACTION_GET_CONTENT).setType("image/*"), PICK)
            "voice" -> listen(activity)
        }
    }

    // --- photo ------------------------------------------------------------------------------------

    private fun cameraFile(activity: Activity) = File(activity.cacheDir, "practice-capture.jpg")
    private fun cameraUri(activity: Activity) = Uri.parse("content://${activity.packageName}.practice-camera/capture.jpg")

    private fun capture(activity: TeninActivity) {
        if (activity.checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            activity.requestPermissions(arrayOf(Manifest.permission.CAMERA), CAMERA_PERMISSION)
            return
        }
        cameraFile(activity).delete()
        try {
            activity.startActivityForResult(Intent(MediaStore.ACTION_IMAGE_CAPTURE).apply {
                putExtra(MediaStore.EXTRA_OUTPUT, cameraUri(activity))
                clipData = ClipData.newRawUri("Scene photo", cameraUri(activity))
                addFlags(GRANTS)
            }, CAMERA)
        } catch (_: ActivityNotFoundException) {
            failed(activity, "No camera app is available.") { start(activity, "library") }
        }
    }

    fun onActivityResult(activity: TeninActivity, code: Int, result: Int, data: Intent?) {
        if (code == CAMERA) activity.revokeUriPermission(cameraUri(activity), GRANTS)
        if (code != CAMERA && code != PICK) return
        if (result != Activity.RESULT_OK) { cameraFile(activity).delete(); return }
        val picked = data?.data
        if (code == PICK && picked == null) return
        val run = ++generation
        worker.execute {
            val image = runCatching {
                if (code == CAMERA) try { PracticePortal.encodePhoto(cameraFile(activity)) } finally { cameraFile(activity).delete() }
                else PracticePortal.encodePhoto(activity, picked!!)
            }.getOrNull()
            ui(activity, run) {
                when {
                    image == null -> failed(activity, "Could not read that photo. Try another one.") { start(activity, "photo") }
                    // Goal already said (Add a photo from the lesson summary): build right away.
                    spokenGoal != null -> buildFromPhoto(activity, image, spokenGoal!!, run)
                    // Otherwise ask what they need to do here, then send the photo with that goal.
                    else -> { pendingPhoto = image; listen(activity) }
                }
            }
        }
    }

    private fun buildFromPhoto(activity: TeninActivity, image: ByteArray, goal: String, run: Int) {
        val preview = BitmapFactory.decodeByteArray(image, 0, image.size)
        val steps = listOf(TeninStep("Photo taken", true), TeninStep("Goal: $goal", true))
        activity.showPhotoProgress(preview, steps + TeninStep("Building your lesson…", false), ready = false,
            onContinue = {}, onRetake = { start(activity, "photo") })
        worker.execute {
            val made = runCatching { PracticePortal.createPractice(activity, image, goal) }
            ui(activity, run) {
                made.onSuccess { practice ->
                    spokenGoal = null
                    pendingPhoto = null
                    PracticePortal.remember(practice)
                    activity.showPhotoProgress(preview, steps + TeninStep(englishTitle(practice.title).let { "Lesson ready: $it" }, true), ready = true,
                        onContinue = { activity.showPutOnGlasses(practice.id, "", practice.title) }, onRetake = { start(activity, "photo") }, update = true)
                }.onFailure {
                    // Try again: same photo and goal. Retake: a new photo.
                    activity.showPhotoProgress(preview, steps, ready = false, error = it.message ?: "Could not build a lesson from that photo.",
                        onContinue = { buildFromPhoto(activity, image, goal, ++generation) }, onRetake = { start(activity, "photo") }, update = true)
                }
            }
        }
    }

    // --- spoken goal --------------------------------------------------------------------------------

    private fun listen(activity: TeninActivity) {
        if (activity.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            activity.requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), MIC_PERMISSION)
            return
        }
        stopRecorder()
        val file = File(activity.cacheDir, "practice-goal.m4a").also { it.delete() }
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
            next.setOnInfoListener { _, what, _ -> if (what == MediaRecorder.MEDIA_RECORDER_INFO_MAX_DURATION_REACHED) finishListening(activity) }
            next.setOutputFile(file.path)
            next.prepare()
            next.start()
        } catch (_: Exception) {
            next.release()
            failed(activity, "Could not start the microphone.") { listen(activity) }
            return
        }
        recorder = next
        recording = file
        val run = ++generation
        activity.showSayGoal("", true, 0f, onMic = { finishListening(activity) }, onClose = { cancel(); activity.onBackPressed() })
        // Live level for the waveform; tapping the mic again finishes.
        fun meter() {
            val current = recorder ?: return
            if (run != generation || activity.isDestroyed) return
            val level = (current.maxAmplitude / 12000f).coerceIn(0f, 1f)
            activity.showSayGoal("", true, level, onMic = { finishListening(activity) }, onClose = { cancel(); activity.onBackPressed() }, update = true)
            main.postDelayed({ meter() }, 250)
        }
        main.postDelayed({ meter() }, 250)
    }

    private fun finishListening(activity: TeninActivity) {
        if (recorder == null) return
        val file = recording
        val ok = stopRecorder()
        if (!ok || file == null || file.length() == 0L) {
            file?.delete()
            failed(activity, "Nothing was recorded. Tap the mic and say what you need to do.") { listen(activity) }
            return
        }
        val run = ++generation
        activity.showSayGoal("Understanding you…", false, 0f, onMic = {}, onClose = { cancel(); activity.onBackPressed() }, update = true, busy = true)
        worker.execute {
            val heard = runCatching { try { PracticePortal.transcribe(activity, file.readBytes(), "") } finally { file.delete() } }
            ui(activity, run) {
                heard.onSuccess { text ->
                    val photo = pendingPhoto
                    if (photo != null) buildFromPhoto(activity, photo, text, run) else writeLesson(activity, text, run)
                }
                    .onFailure { failed(activity, it.message ?: "Could not understand that.") { listen(activity) } }
            }
        }
    }

    private fun writeLesson(activity: TeninActivity, goal: String, run: Int) {
        activity.showSayGoal(goal, false, 0f, onMic = {}, onClose = { cancel(); activity.onBackPressed() }, update = true, busy = true)
        worker.execute {
            val written = runCatching { PracticePortal.createScenario(activity, goal, LEVEL, "") }
            ui(activity, run) {
                written.onSuccess { lesson ->
                    val title = lesson.getString("title")
                    val role = lesson.optJSONObject("partnerRole")?.optString("en").orEmpty()
                    val first = lesson.optJSONArray("beats")?.optJSONObject(0)?.optJSONArray("lines")?.optJSONObject(0)
                    activity.showLessonSummary(iconFor(title + " " + goal), englishTitle(title),
                        if (role.isNotEmpty()) "You talk with the $role" else "",
                        first?.let { "${it.optString("jp")} · ${it.optString("en")}" }.orEmpty(),
                        // Adding a photo grounds the same goal in the real place (photo practice).
                        onAddPhoto = { spokenGoal = goal; start(activity, "photo") },
                        onContinue = { activity.showPutOnGlasses(lesson.getString("id"), lesson.optString("startImage"), title) },
                        onAgain = { listen(activity) })
                }.onFailure { failed(activity, it.message ?: "Could not write a lesson from that.") { listen(activity) } }
            }
        }
    }

    // --- shared -------------------------------------------------------------------------------------

    fun onPermission(activity: TeninActivity, code: Int, grants: IntArray) {
        val granted = grants.firstOrNull() == PackageManager.PERMISSION_GRANTED
        when (code) {
            CAMERA_PERMISSION -> if (granted) capture(activity) else failed(activity, "Allow the camera to take a photo, or choose one from your library.") { start(activity, "library") }
            MIC_PERMISSION -> if (granted) listen(activity) else failed(activity, "Allow the microphone to say your goal.") { listen(activity) }
        }
    }

    /** Stops any recording when the Tenin screen goes away. */
    fun cancel() {
        generation++
        pendingPhoto = null
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
        activity.showPhotoProgress(null, emptyList(), ready = false, error = message, onContinue = retry, onRetake = retry)

    private fun ui(activity: TeninActivity, run: Int, block: () -> Unit) =
        main.post { if (run == generation && !activity.isDestroyed) block() }

    private fun englishTitle(title: String) = title.substringAfter(" — ", title).trim()

    private fun iconFor(text: String) = when {
        Regex("restaurant|food|cafe|café|coffee|order|menu|餐|咖啡|食", RegexOption.IGNORE_CASE).containsMatchIn(text) -> TeninIcon.UTENSILS
        Regex("shop|store|market|buy|supermarket|买|店", RegexOption.IGNORE_CASE).containsMatchIn(text) -> TeninIcon.CART
        Regex("doctor|clinic|hospital|pharmacy|医", RegexOption.IGNORE_CASE).containsMatchIn(text) -> TeninIcon.STETHOSCOPE
        Regex("home|house|家", RegexOption.IGNORE_CASE).containsMatchIn(text) -> TeninIcon.HOME
        else -> TeninIcon.SPARKLE
    }
}
