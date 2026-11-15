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
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import android.media.ExifInterface
import android.net.Uri
import android.os.Bundle
import android.os.ParcelFileDescriptor
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.speech.RecognizerIntent
import android.text.InputFilter
import android.util.Base64
import android.widget.*
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileNotFoundException
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executors
import kotlin.math.roundToInt

private const val CAMERA = 18
private const val CAMERA_PERMISSION = 19
private const val VOICE = 20
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
        private data class Pending(val id: String, val title: String, val image: ByteArray, val expires: Long)
        private data class Session(val id: String, val title: String, val startImage: String = "")
        @Volatile private var ready: List<Pending> = emptyList()
        @Volatile private var selected: Session? = null
        private fun available() = ready.filter { it.expires > System.currentTimeMillis() }
        @JvmStatic fun selectedSession(): Array<String> =
            selected?.let { arrayOf(it.id, it.startImage, it.title) } ?: emptyArray()
        @JvmStatic fun pendingImage(id: String): ByteArray? = available().firstOrNull { it.id == id }?.image
    }

    private val worker = Executors.newSingleThreadExecutor()
    private lateinit var preview: ImageView
    private lateinit var goal: EditText
    private lateinit var state: TextView
    private lateinit var takePhoto: Button
    private lateinit var dictate: Button
    private lateinit var generate: Button
    private lateinit var openGlasses: Button
    private lateinit var sessionPicker: RadioGroup
    private var presets: List<Session> = emptyList()
    private var sessionsByViewId: Map<Int, Session> = emptyMap()
    private var photo: ByteArray? = null
    private var busy = false
    @Volatile private var connection: HttpURLConnection? = null
    @Volatile private var closed = false
    private val cameraUri: Uri get() = Uri.parse("content://$packageName.practice-camera/capture.jpg")
    private val cameraFile: File get() = File(cacheDir, "practice-capture.jpg")

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
        label("Choose a practice session", 20f)
        sessionPicker = RadioGroup(this).apply {
            orientation = RadioGroup.VERTICAL
            panel.addView(this)
        }
        openGlasses = button("Open selected session in glasses") {
            val session = sessionsByViewId[sessionPicker.checkedRadioButtonId] ?: return@button
            if (session.startImage.isBlank() && pendingImage(session.id) == null) {
                refreshSessions()
                state.text = "This photo practice expired. Select another session or create one again."
                return@button
            }
            selected = session
            startActivity(Intent().setClassName(packageName, "ai.nreal.activitylife.NRXRActivity"))
        }
        label("Practice from your surroundings", 20f)
        label("Take a photo, add a goal, then create a short Japanese practice.", 16f)
        preview = ImageView(this).apply {
            contentDescription = "Scene photo preview"
            scaleType = ImageView.ScaleType.FIT_CENTER
            visibility = android.view.View.GONE
            panel.addView(this, LinearLayout.LayoutParams(-1, dp(190f)))
        }
        takePhoto = button("Take scene photo", ::capture)
        goal = EditText(this).apply {
            hint = "What would you like to practice?"
            filters = arrayOf(InputFilter.LengthFilter(500))
            inputType = android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
            panel.addView(this)
        }
        dictate = button("Say my goal") {
            try {
                startActivityForResult(Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
                    .putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM), VOICE)
            } catch (_: android.content.ActivityNotFoundException) {
                state.text = "Speech input is unavailable. Type your goal instead."
            }
        }
        generate = button("Upload and create practice", ::upload)
        state = label("", 16f).apply { accessibilityLiveRegion = android.view.View.ACCESSIBILITY_LIVE_REGION_POLITE }
        presets = loadPresets()
        refreshSessions(selected?.id)
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
        val photoSessions = available().map { Session(it.id, it.title) }
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
        dictate.isEnabled = !busy
        goal.isEnabled = !busy
        generate.isEnabled = !busy && photo != null
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
    }

    @Deprecated("Uses Android's external camera and speech activities.")
    override fun onActivityResult(code: Int, result: Int, data: Intent?) {
        super.onActivityResult(code, result, data)
        if (code == VOICE && result == RESULT_OK) {
            data?.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS)?.firstOrNull()?.let { goal.setText(it) }
        }
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
            val bytes = try { encodePhoto(cameraFile) } catch (_: Exception) { null }
            finally { cameraFile.delete() }
            runOnUiThread {
                if (closed) return@runOnUiThread
                if (bytes != null) showPhoto(bytes)
                state.text = if (bytes != null) "Photo ready. Enter your goal and upload." else "Could not read the photo. Try again."
                busy = false
                updateButtons()
            }
        }
    }

    private fun showPhoto(bytes: ByteArray) {
        photo = bytes
        preview.setImageBitmap(BitmapFactory.decodeByteArray(bytes, 0, bytes.size))
        preview.visibility = android.view.View.VISIBLE
        takePhoto.text = "Retake photo"
    }

    private fun encodePhoto(file: File): ByteArray {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.path, bounds)
        var sample = 1
        while (maxOf(bounds.outWidth, bounds.outHeight) / sample > 2000) sample *= 2
        val bitmap = BitmapFactory.decodeFile(file.path, BitmapFactory.Options().apply { inSampleSize = sample })
            ?: throw IllegalArgumentException("No photo")
        val orientation = ExifInterface(file.path).getAttributeInt(
            ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL)
        val transform = Matrix().apply {
            when (orientation) {
                ExifInterface.ORIENTATION_FLIP_HORIZONTAL -> postScale(-1f, 1f)
                ExifInterface.ORIENTATION_ROTATE_180 -> postRotate(180f)
                ExifInterface.ORIENTATION_FLIP_VERTICAL -> postScale(1f, -1f)
                ExifInterface.ORIENTATION_TRANSPOSE -> { postScale(-1f, 1f); postRotate(270f) }
                ExifInterface.ORIENTATION_ROTATE_90 -> postRotate(90f)
                ExifInterface.ORIENTATION_TRANSVERSE -> { postScale(-1f, 1f); postRotate(90f) }
                ExifInterface.ORIENTATION_ROTATE_270 -> postRotate(270f)
            }
            val scale = minOf(1f, 1280f / maxOf(bitmap.width, bitmap.height))
            postScale(scale, scale)
        }
        val upright = Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, transform, true)
        return try {
            ByteArrayOutputStream().use { out -> upright.compress(Bitmap.CompressFormat.JPEG, 87, out); out.toByteArray() }
        } finally {
            if (upright !== bitmap) upright.recycle()
            bitmap.recycle()
        }
    }

    private fun upload() {
        val image = photo ?: return
        val requestGoal = goal.text.toString().trim()
        if (requestGoal.isEmpty()) { goal.error = "Enter or say a practice goal"; return }
        busy = true
        state.text = "Uploading photo and creating practice…"
        updateButtons()
        worker.execute {
            val result = runCatching { createPractice(image, requestGoal) }
            runOnUiThread {
                if (closed) return@runOnUiThread
                result.onSuccess {
                    ready = (listOf(it) + available()).take(5)
                    refreshSessions(it.id)
                    state.text = "Ready: ${it.title}. This session is selected above."
                }.onFailure { state.text = it.message ?: "Could not upload the photo. Try again." }
                busy = false
                updateButtons()
            }
        }
    }

    private fun createPractice(image: ByteArray, goal: String): Pending {
        val config = JSONObject(assets.open("visko.json").bufferedReader().use { it.readText() })
        val url = URL(config.getString("portalUrl").trimEnd('/') + "/api/practice/generate")
        val request = url.openConnection() as HttpURLConnection
        connection = request
        try {
            if (closed) throw IllegalStateException("Upload cancelled.")
            request.instanceFollowRedirects = false
            request.requestMethod = "POST"
            request.connectTimeout = 7000
            request.readTimeout = 40000
            request.doOutput = true
            request.setRequestProperty("Content-Type", "application/json")
            config.optString("portalToken").takeIf { it.isNotBlank() }?.let { request.setRequestProperty("portal_token", it) }
            val body = JSONObject().put("image", "data:image/jpeg;base64," + Base64.encodeToString(image, Base64.NO_WRAP)).put("goal", goal).toString().toByteArray(Charsets.UTF_8)
            request.setFixedLengthStreamingMode(body.size)
            request.outputStream.use { it.write(body) }
            val status = request.responseCode
            if (status != 200) {
                val message = when (status) {
                    401 -> "The Portal rejected this app's access. Check the Portal configuration."
                    429 -> "The Portal is busy. Wait a moment and try again."
                    in 500..599 -> "The lesson service couldn't create a practice. Your photo is still here; try again."
                    else -> "The Portal couldn't use this request. Check your photo and goal."
                }
                throw IllegalStateException(message)
            }
            val result = JSONObject(request.inputStream.bufferedReader().use { it.readText() })
            val lesson = result.getJSONObject("lesson")
            return Pending(lesson.getString("id"), lesson.getString("title"), image, result.getLong("expiresAt"))
        } catch (_: java.io.IOException) {
            throw IllegalStateException("Could not reach the Portal. Check Wi-Fi and try again.")
        } finally {
            connection = null
            request.disconnect()
        }
    }

    override fun onDestroy() {
        closed = true
        connection?.disconnect()
        worker.shutdownNow()
        super.onDestroy()
    }
}
