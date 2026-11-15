package com.immersethebay.phone

import android.Manifest
import android.app.Activity
import android.content.ClipData
import android.content.ContentProvider
import android.content.ContentValues
import android.content.Intent
import android.content.pm.PackageManager
import android.database.Cursor
import android.database.MatrixCursor
import android.graphics.BitmapFactory
import android.media.MediaRecorder
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.text.InputFilter
import android.view.MotionEvent
import android.widget.*
import org.json.JSONObject
import java.io.File
import java.io.FileNotFoundException
import java.net.HttpURLConnection
import java.util.concurrent.Executors
import kotlin.math.roundToInt

private const val CAMERA = 18
private const val CAMERA_PERMISSION = 19
private const val MIC_PERMISSION = 21
private const val HOLD_LABEL = "Hold to say your goal"
private const val GRANTS = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION

/** One private cache file exposed only to the camera activity holding a temporary URI grant. */
class PracticeCameraOutput : ContentProvider() {
    private fun file(uri: Uri): File {
        if (uri.path != "/capture.jpg") throw FileNotFoundException()
        return File(requireNotNull(context).cacheDir, "practice-capture.jpg")
    }

    override fun onCreate() = true
    override fun getType(uri: Uri) = "image/jpeg"
    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor =
        ParcelFileDescriptor.open(file(uri), ParcelFileDescriptor.parseMode(mode))

    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor {
        val source = file(uri)
        val columns = projection?.toList()?.toTypedArray() ?: arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)
        return MatrixCursor(columns).apply {
            addRow(columns.map { if (it == OpenableColumns.DISPLAY_NAME) "scene.jpg" else if (it == OpenableColumns.SIZE) source.length() else null })
        }
    }

    override fun insert(uri: Uri, values: ContentValues?): Uri? = null
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?) = 0
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?) = 0
}

class PhonePracticeActivity : Activity() {
    companion object {
        private data class Session(val id: String, val title: String, val startImage: String = "")
        // Unity reads the hand-off through these statics; both phone screens share PracticePortal.
        @JvmStatic fun selectedSession(): Array<String> = PracticePortal.selectedSession()
        @JvmStatic fun pendingImage(id: String): ByteArray? = PracticePortal.pendingImage(id)
        @JvmStatic fun consumeLaunchRequest(): Boolean = PracticePortal.consumeLaunchRequest()
        @JvmStatic fun consumeEndRequest(): Boolean = PracticePortal.consumeEndRequest()
    }

    private val worker = Executors.newSingleThreadExecutor()
    private lateinit var preview: ImageView
    private lateinit var goal: EditText
    private lateinit var state: TextView
    private lateinit var takePhoto: Button
    private lateinit var holdGoal: Button
    private lateinit var language: RadioGroup
    private lateinit var generate: Button
    private lateinit var openGlasses: Button
    private lateinit var sessionPicker: RadioGroup
    private var presets: List<Session> = emptyList()
    private var sessionsByViewId: Map<Int, Session> = emptyMap()
    private var photo: ByteArray? = null
    private var busy = false
    private var recorder: MediaRecorder? = null
    private var recordStartedAt = 0L
    private var latched = false
    @Volatile private var connection: HttpURLConnection? = null
    @Volatile private var closed = false
    private val cameraUri: Uri get() = Uri.parse("content://$packageName.practice-camera/capture.jpg")
    private val cameraFile: File get() = File(cacheDir, "practice-capture.jpg")
    private val goalAudio: File get() = File(cacheDir, "practice-goal.m4a")

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val panel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            val gap = dp(20f)
            setPadding(gap, gap, gap, gap)
        }
        fun label(text: String, size: Float) = TextView(this).apply { this.text = text; textSize = size; panel.addView(this) }
        fun button(text: String, onClick: () -> Unit) = Button(this).apply {
            this.text = text
            setOnClickListener { onClick() }
            panel.addView(this)
        }
        label("ImmerseTheBay", 24f)
        // Same flow as the web app: photo + spoken goal create the practice automatically.
        label("Practise where you are", 20f)
        label("Take a photo, then hold the button and say what you want to practise. The practice is created for you.", 16f)
        preview = ImageView(this).apply {
            contentDescription = "Scene photo preview"
            scaleType = ImageView.ScaleType.FIT_CENTER
            visibility = android.view.View.GONE
            panel.addView(this, LinearLayout.LayoutParams(-1, dp(190f)))
        }
        takePhoto = button("Take scene photo", ::capture)
        language = RadioGroup(this).apply {
            orientation = RadioGroup.HORIZONTAL
            contentDescription = "Language you will speak"
            listOf("zh" to "中文", "en" to "English", "ja" to "日本語").forEach { (code, name) ->
                addView(RadioButton(this@PhonePracticeActivity).apply { id = android.view.View.generateViewId(); text = name; tag = code })
            }
            check(getChildAt(0).id)
            panel.addView(this)
        }
        holdGoal = button(HOLD_LABEL) {}
        holdGoal.setOnTouchListener { view, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> { if (recorder != null && latched) stopGoalRecording() else startGoalRecording(); true }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    // Hold and release, or tap to start and tap again to finish.
                    if (recorder != null && !latched) {
                        if (SystemClock.elapsedRealtime() - recordStartedAt < 350) {
                            latched = true
                            holdGoal.text = "Tap to finish"
                            state.text = "Listening… tap again when you are done."
                        } else stopGoalRecording()
                    }
                    if (event.actionMasked == MotionEvent.ACTION_UP) view.performClick()
                    true
                }
                else -> false
            }
        }
        goal = EditText(this).apply {
            hint = "Or type what you want to practise"
            filters = arrayOf(InputFilter.LengthFilter(500))
            inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
            panel.addView(this)
        }
        generate = button("Create practice", ::upload)
        state = label("", 16f).apply { accessibilityLiveRegion = android.view.View.ACCESSIBILITY_LIVE_REGION_POLITE }
        openGlasses = button("Start in glasses") {
            val session = sessionsByViewId[sessionPicker.checkedRadioButtonId] ?: return@button
            if (session.startImage.isBlank() && PracticePortal.pendingImage(session.id) == null) {
                refreshSessions()
                state.text = "This photo practice expired. Select another session or create one again."
                return@button
            }
            PracticePortal.select(session.id, session.startImage, session.title)
            PracticePortal.requestLaunch()
            startActivity(Intent().setClassName(packageName, "ai.nreal.activitylife.NRXRActivity"))
        }
        label("Or choose a session", 20f)
        sessionPicker = RadioGroup(this).apply {
            orientation = RadioGroup.VERTICAL
            panel.addView(this)
        }
        presets = loadPresets()
        refreshSessions(PracticePortal.selectedSession().getOrNull(0))
        sessionPicker.setOnCheckedChangeListener { _, _ -> updateButtons() }
        setContentView(ScrollView(this).apply { addView(panel); isFillViewport = true })
        updateButtons()
    }

    private fun loadPresets(): List<Session> = assets.list("lessons").orEmpty()
        .filter { it.endsWith(".json") }
        .mapNotNull { name ->
            runCatching {
                val lesson = JSONObject(assets.open("lessons/$name").bufferedReader().use { it.readText() })
                val title = lesson.getString("title").substringAfter(" — ")
                Session(lesson.getString("id"), title, lesson.getString("startImage"))
            }.getOrNull()
        }.sortedBy { it.id != "ja-konbini-001" }

    private fun refreshSessions(preferredId: String? = sessionsByViewId[sessionPicker.checkedRadioButtonId]?.id) {
        val photoSessions = PracticePortal.available().map { Session(it.id, it.title) }
        val choices = presets + photoSessions
        sessionPicker.clearCheck()
        sessionPicker.removeAllViews()
        sessionsByViewId = choices.associateBy { session ->
            val item = RadioButton(this).apply {
                id = android.view.View.generateViewId()
                text = if (session.startImage.isBlank()) "Photo · ${session.title}" else session.title
            }
            sessionPicker.addView(item)
            item.id
        }
        (sessionsByViewId.entries.firstOrNull { it.value.id == preferredId } ?: sessionsByViewId.entries.firstOrNull())
            ?.let { sessionPicker.check(it.key) }
    }

    private fun dp(value: Float) = (resources.displayMetrics.density * value).roundToInt()

    private fun updateButtons() {
        takePhoto.isEnabled = !busy
        holdGoal.isEnabled = !busy || recorder != null
        for (index in 0 until language.childCount) language.getChildAt(index).isEnabled = !busy && recorder == null
        goal.isEnabled = !busy && recorder == null
        generate.isEnabled = !busy && recorder == null && photo != null
        openGlasses.isEnabled = !busy && sessionPicker.checkedRadioButtonId != -1
    }

    private fun capture() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.CAMERA), CAMERA_PERMISSION)
            return
        }
        cameraFile.delete()
        try {
            startActivityForResult(Intent(MediaStore.ACTION_IMAGE_CAPTURE).apply {
                putExtra(MediaStore.EXTRA_OUTPUT, cameraUri)
                clipData = ClipData.newRawUri("Scene photo", cameraUri)
                addFlags(GRANTS)
            }, CAMERA)
        } catch (_: android.content.ActivityNotFoundException) {
            state.text = "No camera app is available."
        }
    }

    override fun onRequestPermissionsResult(code: Int, permissions: Array<String>, grants: IntArray) {
        super.onRequestPermissionsResult(code, permissions, grants)
        if (code == CAMERA_PERMISSION && grants.firstOrNull() == PackageManager.PERMISSION_GRANTED) capture()
        else if (code == CAMERA_PERMISSION) state.text = "Allow camera access to take a scene photo."
        if (code == MIC_PERMISSION) state.text = if (grants.firstOrNull() == PackageManager.PERMISSION_GRANTED)
            "Microphone allowed. Hold the button and say your goal." else "Allow the microphone to say your goal, or type it instead."
    }

    @Deprecated("Uses Android's external camera and speech activities.")
    override fun onActivityResult(code: Int, result: Int, data: Intent?) {
        super.onActivityResult(code, result, data)
        if (code != CAMERA) return
        revokeUriPermission(cameraUri, GRANTS)
        if (result != RESULT_OK) {
            cameraFile.delete()
            return
        }
        busy = true
        state.text = "Preparing photo…"
        updateButtons()
        worker.execute {
            val bytes = try { PracticePortal.encodePhoto(cameraFile) } catch (_: Exception) { null }
            finally { cameraFile.delete() }
            runOnUiThread {
                if (closed) return@runOnUiThread
                if (bytes != null) showPhoto(bytes)
                state.text = if (bytes != null) "Photo ready. Hold the button and say your goal." else "Could not read the photo. Try again."
                busy = false
                updateButtons()
                // A goal said or typed before the photo creates the practice as soon as the photo is ready.
                if (bytes != null && goal.text.isNotBlank()) upload()
            }
        }
    }

    private fun showPhoto(bytes: ByteArray) {
        photo = bytes
        preview.setImageBitmap(BitmapFactory.decodeByteArray(bytes, 0, bytes.size))
        preview.visibility = android.view.View.VISIBLE
        takePhoto.text = "Retake photo"
    }


    private fun upload() {
        val image = photo ?: return
        val requestGoal = goal.text.toString().trim()
        if (requestGoal.isEmpty()) { goal.error = "Enter or say a practice goal"; return }
        busy = true
        state.text = "Uploading photo and creating practice…"
        updateButtons()
        worker.execute {
            val result = runCatching { if (closed) error("Upload cancelled."); PracticePortal.createPractice(this, image, requestGoal) { connection = it } }
            runOnUiThread {
                if (closed) return@runOnUiThread
                result.onSuccess {
                    PracticePortal.remember(it)
                    refreshSessions(it.id)
                    state.text = "Ready: ${it.title}. Put on the glasses and tap Start in glasses."
                }.onFailure { state.text = it.message ?: "Could not upload the photo. Try again." }
                busy = false
                updateButtons()
            }
        }
    }

    private fun startGoalRecording() {
        if (busy || recorder != null) return
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), MIC_PERMISSION)
            return
        }
        goalAudio.delete()
        @Suppress("DEPRECATION")
        val next = if (Build.VERSION.SDK_INT >= 31) MediaRecorder(this) else MediaRecorder()
        try {
            next.setAudioSource(MediaRecorder.AudioSource.MIC)
            next.setOutputFormat(MediaRecorder.OutputFormat.MPEG_4)
            next.setAudioEncoder(MediaRecorder.AudioEncoder.AAC)
            next.setAudioChannels(1)
            next.setAudioSamplingRate(16000)
            next.setAudioEncodingBitRate(64000)
            next.setMaxDuration(30000)
            next.setOnInfoListener { _, what, _ -> if (what == MediaRecorder.MEDIA_RECORDER_INFO_MAX_DURATION_REACHED) stopGoalRecording() }
            next.setOutputFile(goalAudio.path)
            next.prepare()
            next.start()
        } catch (_: Exception) {
            next.release()
            state.text = "Could not start the microphone. Type your goal instead."
            return
        }
        recorder = next
        recordStartedAt = SystemClock.elapsedRealtime()
        latched = false
        holdGoal.text = "Release to finish"
        state.text = "Listening… say what you want to practise."
        updateButtons()
    }

    private fun stopGoalRecording() {
        val current = recorder ?: return
        recorder = null
        latched = false
        holdGoal.text = HOLD_LABEL
        // stop() throws when almost nothing was recorded.
        val recorded = try { current.stop(); true } catch (_: RuntimeException) { false } finally { current.release() }
        if (!recorded || goalAudio.length() == 0L) {
            goalAudio.delete()
            state.text = "Nothing was recorded. Hold the button while you speak."
            updateButtons()
            return
        }
        val code = language.findViewById<RadioButton>(language.checkedRadioButtonId)?.tag as? String ?: "zh"
        busy = true
        state.text = "Got it · understanding you…"
        updateButtons()
        worker.execute {
            val result = runCatching { try { PracticePortal.transcribe(this, goalAudio.readBytes(), code) { connection = it } } finally { goalAudio.delete() } }
            runOnUiThread {
                if (closed) return@runOnUiThread
                busy = false
                result.onSuccess { text ->
                    goal.setText(text)
                    if (photo != null) upload() else { state.text = "“$text” · now take a photo of where you are."; updateButtons() }
                }.onFailure { state.text = it.message ?: "Could not understand the recording. Try again or type your goal."; updateButtons() }
            }
        }
    }



    override fun onDestroy() {
        closed = true
        recorder?.let { runCatching { it.stop() }; it.release() }
        recorder = null
        goalAudio.delete()
        connection?.disconnect()
        worker.shutdownNow()
        super.onDestroy()
    }
}
