package com.immersethebay.phone

import android.Manifest
import android.app.Activity
import android.app.Dialog
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.Matrix
import android.graphics.Rect
import android.graphics.SurfaceTexture
import android.hardware.camera2.CameraCaptureSession
import android.hardware.camera2.CameraCharacteristics
import android.hardware.camera2.CameraDevice
import android.hardware.camera2.CameraManager
import android.hardware.camera2.CaptureRequest
import android.hardware.display.DisplayManager
import android.media.MediaRecorder
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.util.Size
import android.util.TypedValue
import android.view.Gravity
import android.view.PixelCopy
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.Window
import android.widget.LinearLayout
import java.io.File
import java.net.HttpURLConnection
import java.util.concurrent.CancellationException
import java.util.concurrent.Executors
import kotlin.math.abs

/**
 * Drives the Tenin Live Lesson screens with the phone workflow: camera or library photo → photo practice,
 * spoken goal → written lesson (optionally grounded with a photo afterwards), and the glasses hand-off.
 */
object TeninWorkflow {
    private const val PICK = 32
    private const val CAMERA_PERMISSION = 33
    private const val MIC_PERMISSION = 34
    private const val LEVEL = "N5"

    private val worker = Executors.newSingleThreadExecutor()
    private val disconnectWorker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private var owner: TeninActivity? = null
    private var expectedResult: Int? = null
    private var camera: ScenePhotoCamera? = null
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
            "library" -> pickPhoto(activity)
            "voice" -> showGoalDraft(activity)
        }
    }

    // --- photo ------------------------------------------------------------------------------------

    private fun capture(activity: TeninActivity) {
        if (activity.checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            expectedPermission = CAMERA_PERMISSION
            activity.requestPermissions(arrayOf(Manifest.permission.CAMERA), CAMERA_PERMISSION)
            return
        }
        closeCamera()
        val run = generation
        val dialog = ScenePhotoCamera(activity, main, onPhoto = { frame ->
            if (activity === owner && run == generation) {
                preparePhoto(activity) {
                    try { PracticePortal.encodeCroppedPhoto(frame) }
                    finally { frame.recycle() }
                }
            } else frame.recycle()
        }, onFailure = {
            if (activity === owner && run == generation)
                failed(activity, "The camera is unavailable. Try again, or choose a photo from your library.") {
                    capture(activity)
                }
        })
        camera = dialog
        dialog.open()
    }

    private fun closeCamera() {
        val current = camera
        camera = null
        current?.dismiss()
    }

    private fun pickPhoto(activity: TeninActivity) {
        expectedResult = PICK
        try {
            activity.startActivityForResult(Intent(Intent.ACTION_GET_CONTENT).setType("image/*"), PICK)
        } catch (_: ActivityNotFoundException) {
            expectedResult = null
            failed(activity, "No image picker is available.") { start(activity, "photo", spokenGoal) }
        }
    }

    fun onActivityResult(activity: TeninActivity, code: Int, result: Int, data: Intent?) {
        if (activity !== owner || code != PICK || code != expectedResult) return
        expectedResult = null
        if (result != Activity.RESULT_OK) return
        val picked = data?.data ?: return
        preparePhoto(activity) { PracticePortal.encodePhoto(activity, picked) }
    }

    private fun preparePhoto(activity: TeninActivity, encode: () -> ByteArray) {
        closeCamera()
        activity.showPhotoProgress(null, listOf(TeninStep("Preparing your photo…", false)), ready = false,
            onContinue = {}, onRetake = {})
        val run = ++generation
        worker.execute {
            val prepared = runCatching {
                val image = encode()
                PreparedPhoto(image, requireNotNull(BitmapFactory.decodeByteArray(image, 0, image.size)))
            }.getOrNull()
            ui(activity, run) {
                when {
                    prepared == null -> failed(activity, "Could not read that photo. Try another one.") { capture(activity) }
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

    /** Release capture and recording in the background. */
    fun pause(activity: TeninActivity) {
        if (activity !== owner) return
        closeCamera()
        if (recorder == null && recognizer == null) return
        generation++
        releaseRecognizer()
        discardRecording()
        showGoalDraft(activity, "Recording stopped while the app was in the background.")
    }

    /** Cancels one preparation when its screen or owning activity goes away. */
    fun cancel(activity: TeninActivity) {
        if (activity !== owner) return
        owner = null
        closeCamera()
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

/** A 16:9 viewfinder that saves the rendered rear-camera crop. All callbacks use main. */
private class ScenePhotoCamera(
    private val activity: TeninActivity,
    private val main: Handler,
    private val onPhoto: (Bitmap) -> Unit,
    private val onFailure: () -> Unit,
) : TextureView.SurfaceTextureListener, DisplayManager.DisplayListener {
    private val dialog = Dialog(activity)
    private val displays = activity.getSystemService(DisplayManager::class.java)
    private val previousOrientation = activity.requestedOrientation
    private val preview = object : TextureView(activity) {
        override fun onMeasure(widthSpec: Int, heightSpec: Int) {
            var units = MeasureSpec.getSize(widthSpec) / 16
            val configuration = resources.configuration
            if (configuration.orientation == Configuration.ORIENTATION_LANDSCAPE) {
                val windowHeight = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP,
                    configuration.screenHeightDp.toFloat(), resources.displayMetrics)
                units = minOf(units, (windowHeight * 0.40f).toInt() / 9)
            }
            super.onMeasure(MeasureSpec.makeMeasureSpec(units * 16, MeasureSpec.EXACTLY),
                MeasureSpec.makeMeasureSpec(units * 9, MeasureSpec.EXACTLY))
        }
    }
    private lateinit var shutter: View
    private var device: CameraDevice? = null
    private var session: CameraCaptureSession? = null
    private var surface: Surface? = null
    private var bufferSize: Size? = null
    private var sensorOrientation = 0
    private var frameDeadline: Runnable? = null
    private var generation = 0
    private var closed = false
    private var snapshotPending = false

    fun open() {
        // The phone app stays portrait outside capture. Let the viewfinder follow the rear camera
        // while it is open so a physical landscape shot gets the matching display rotation.
        activity.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_FULL_SENSOR
        dialog.requestWindowFeature(Window.FEATURE_NO_TITLE)
        dialog.setOnDismissListener { dismiss() }
        preview.surfaceTextureListener = this
        preview.contentDescription = "16:9 camera preview"
        TeninUi(activity).run {
            shutter = primaryButton("Capture photo", TeninIcon.CAMERA, ::snapshot).apply { isEnabled = false }
            dialog.setContentView(screen(listOf(
                header(back = ::dismiss, close = true),
                title("Take a scene photo").margins(top = 18f),
                body("Frame your scene inside the preview.").margins(top = 8f),
                preview.fillWidth().margins(top = 16f).apply {
                    (layoutParams as LinearLayout.LayoutParams).gravity = Gravity.CENTER_HORIZONTAL
                },
            ), footer = listOf(shutter)))
        }
        displays.registerDisplayListener(this, main)
        dialog.show()
        dialog.window?.setLayout(MATCH_PARENT, MATCH_PARENT)
    }

    override fun onSurfaceTextureAvailable(texture: SurfaceTexture, width: Int, height: Int) {
        if (closed) return
        val run = ++generation
        val manager = activity.getSystemService(CameraManager::class.java)
        frameDeadline = Runnable { fail(run) }.also { main.postDelayed(it, 6000) }
        try {
            val rear = manager.cameraIdList.firstOrNull { id ->
                manager.getCameraCharacteristics(id).get(CameraCharacteristics.LENS_FACING) == CameraCharacteristics.LENS_FACING_BACK
            } ?: error("Rear camera unavailable")
            val settings = manager.getCameraCharacteristics(rear)
            val sizes = settings.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
                ?.getOutputSizes(SurfaceTexture::class.java).orEmpty()
            val ordinary = sizes.filter { maxOf(it.width, it.height) <= 1920 }
            // A 4:3 stream retains more of the upright image width when the phone is vertical.
            val size = ordinary.ifEmpty { sizes.toList() }
                .minWithOrNull(compareBy<Size> { abs(it.width / it.height.toFloat() - 4f / 3f) }
                    .thenBy { abs(minOf(it.width, it.height) - 1080) }) ?: error("No preview size")
            bufferSize = size
            sensorOrientation = settings.get(CameraCharacteristics.SENSOR_ORIENTATION) ?: 0
            texture.setDefaultBufferSize(size.width, size.height)
            updateTransform(width, height)
            val target = Surface(texture).also { surface = it }
            manager.openCamera(rear, object : CameraDevice.StateCallback() {
                override fun onOpened(camera: CameraDevice) {
                    if (!active(run)) { camera.close(); return }
                    device = camera
                    previewCamera(camera, target, settings, run)
                }
                override fun onDisconnected(camera: CameraDevice) { camera.close(); fail(run) }
                override fun onError(camera: CameraDevice, error: Int) { camera.close(); fail(run) }
            }, main)
        } catch (_: Exception) { fail(run) }
    }

    @Suppress("DEPRECATION")
    private fun previewCamera(camera: CameraDevice, target: Surface, settings: CameraCharacteristics, run: Int) {
        try {
            val request = camera.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
                addTarget(target)
                // The TextureView applies our crop. Avoid the camera service also rotating and
                // cropping the buffer on Android when the app has an external display attached.
                if (Build.VERSION.SDK_INT >= 31 && settings.get(CameraCharacteristics.SCALER_AVAILABLE_ROTATE_AND_CROP_MODES)
                        ?.contains(CaptureRequest.SCALER_ROTATE_AND_CROP_NONE) == true)
                    set(CaptureRequest.SCALER_ROTATE_AND_CROP, CaptureRequest.SCALER_ROTATE_AND_CROP_NONE)
                if (settings.get(CameraCharacteristics.CONTROL_AF_AVAILABLE_MODES)
                        ?.contains(CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE) == true)
                    set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE)
            }.build()
            camera.createCaptureSession(listOf(target), object : CameraCaptureSession.StateCallback() {
                override fun onConfigured(configured: CameraCaptureSession) {
                    if (!active(run)) { configured.close(); return }
                    session = configured
                    try { configured.setRepeatingRequest(request, null, main) }
                    catch (_: Exception) { fail(run) }
                }
                override fun onConfigureFailed(configured: CameraCaptureSession) {
                    configured.close()
                    fail(run)
                }
            }, main)
        } catch (_: Exception) { fail(run) }
    }

    override fun onSurfaceTextureSizeChanged(texture: SurfaceTexture, width: Int, height: Int) =
        updateTransform(width, height)

    override fun onSurfaceTextureUpdated(texture: SurfaceTexture) {
        if (closed || snapshotPending || session == null || texture.timestamp <= 0L || shutter.isEnabled) return
        clearFrameDeadline()
        shutter.isEnabled = true
    }

    override fun onSurfaceTextureDestroyed(texture: SurfaceTexture): Boolean {
        releaseCamera()
        return true
    }

    override fun onDisplayAdded(displayId: Int) = Unit
    override fun onDisplayRemoved(displayId: Int) = Unit
    override fun onDisplayChanged(displayId: Int) {
        if (displayId == preview.display?.displayId) updateTransform(preview.width, preview.height)
    }

    private fun updateTransform(width: Int, height: Int) {
        val size = bufferSize ?: return
        if (width <= 0 || height <= 0) return
        val rotation = (preview.display?.rotation ?: Surface.ROTATION_0) * 90
        // TextureView applies sensor orientation first. Undo its independent X/Y stretch,
        // then apply a uniform center fill and compensate for display rotation.
        val swapped = sensorOrientation % 180 != 0
        val imageWidth = if (swapped) size.height else size.width
        val imageHeight = if (swapped) size.width else size.height
        val quarterTurn = rotation % 180 != 0
        val targetWidth = if (quarterTurn) height else width
        val targetHeight = if (quarterTurn) width else height
        val scale = maxOf(targetWidth.toFloat() / imageWidth, targetHeight.toFloat() / imageHeight)
        val pivotX = width / 2f
        val pivotY = height / 2f
        preview.setTransform(Matrix().apply {
            setScale(scale * imageWidth / width, scale * imageHeight / height, pivotX, pivotY)
            postRotate(-rotation.toFloat(), pivotX, pivotY)
        })
    }

    private fun snapshot() {
        if (snapshotPending || !shutter.isEnabled || preview.surfaceTexture?.timestamp?.let { it > 0L } != true) return
        val run = generation
        val window = dialog.window ?: return fail(run)
        val width = preview.width
        val height = preview.height
        val rotation = preview.display?.rotation
        if (width <= 0 || width * 9 != height * 16) return fail(run)
        val location = IntArray(2)
        preview.getLocationInWindow(location)
        val bounds = Rect(location[0], location[1], location[0] + width, location[1] + height)
        val visible = Rect()
        if (!preview.getLocalVisibleRect(visible) || visible.width() != width || visible.height() != height)
            return fail(run)
        val frame = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        snapshotPending = true
        shutter.isEnabled = false
        try {
            // Copy the composed window region, including the applied camera crop.
            // TextureView.bitmap can scale the camera buffer directly to the destination.
            PixelCopy.request(window, bounds, frame, { result ->
                snapshotPending = false
                if (!active(run) || preview.display?.rotation != rotation ||
                    preview.width != width || preview.height != height) {
                    frame.recycle()
                    if (active(run)) shutter.isEnabled = true
                } else if (result != PixelCopy.SUCCESS ||
                    Color.alpha(frame.getPixel(width / 2, height / 2)) == 0) {
                    frame.recycle()
                    fail(run)
                } else {
                    dismiss()
                    onPhoto(frame)
                }
            }, main)
        } catch (_: IllegalArgumentException) {
            snapshotPending = false
            frame.recycle()
            fail(run)
        }
    }

    private fun active(run: Int) = !closed && run == generation

    private fun fail(run: Int) {
        if (!active(run)) return
        dismiss()
        onFailure()
    }

    private fun clearFrameDeadline() {
        frameDeadline?.let(main::removeCallbacks)
        frameDeadline = null
    }

    private fun releaseCamera() {
        generation++
        clearFrameDeadline()
        if (::shutter.isInitialized) shutter.isEnabled = false
        session?.close()
        session = null
        device?.close()
        device = null
        surface?.release()
        surface = null
    }

    fun dismiss() {
        if (closed) return
        closed = true
        displays.unregisterDisplayListener(this)
        releaseCamera()
        dialog.dismiss()
        activity.requestedOrientation = previousOrientation
    }
}
