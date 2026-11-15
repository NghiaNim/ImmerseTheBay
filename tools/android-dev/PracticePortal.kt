package com.immersethebay.phone

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import android.media.ExifInterface
import android.net.Uri
import android.util.Base64
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/**
 * Portal calls and the phone-to-glasses hand-off shared by the phone screens (PhonePracticeActivity and
 * the Tenin UI). Unity reads the hand-off through PhonePracticeActivity's static methods.
 */
object PracticePortal {
    class Practice(val id: String, val title: String, val image: ByteArray, val expires: Long)

    @Volatile private var ready: List<Practice> = emptyList()
    @Volatile private var launchRequested = false
    @Volatile private var endRequested = false

    // --- hand-off to the glasses ----------------------------------------------------------------

    /** Photo practices stay on the phone; their JPEG goes to the glasses as the start image. */
    fun remember(practice: Practice) { ready = (listOf(practice) + available()).take(5) }
    fun available(): List<Practice> = ready.filter { it.expires > System.currentTimeMillis() }
    fun pendingImage(id: String): ByteArray? = available().firstOrNull { it.id == id }?.image

    /** The lesson chosen on either phone screen: id, start image and title. */
    fun select(id: String, startImage: String, title: String) { TeninHooks.selected = arrayOf(id, startImage, title) }
    fun selectedSession(): Array<String> = TeninHooks.selected

    /** Read once by the glasses so one tap on the phone starts or ends the session there. */
    fun requestLaunch() { launchRequested = true }
    fun consumeLaunchRequest(): Boolean = launchRequested.also { launchRequested = false }
    fun requestEnd() { endRequested = true }
    fun consumeEndRequest(): Boolean = endRequested.also { endRequested = false }

    // --- photos ---------------------------------------------------------------------------------

    /** Upright, center-cropped 16:9 JPEG, at most 1280 × 720. */
    fun encodePhoto(file: File): ByteArray {
        val orientation = ExifInterface(file.path).getAttributeInt(
            ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL)
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.path, bounds)
        var sample = 1
        while (maxOf(bounds.outWidth, bounds.outHeight) / sample > 4096 ||
            (bounds.outWidth / sample).toLong() * (bounds.outHeight / sample) > 8_000_000L) sample *= 2
        val bitmap = BitmapFactory.decodeFile(file.path, BitmapFactory.Options().apply { inSampleSize = sample })
            ?: throw IllegalArgumentException("No photo")
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
        }
        var upright: Bitmap = bitmap
        var cropped: Bitmap = bitmap
        var output: Bitmap = bitmap
        try {
            upright = Bitmap.createBitmap(bitmap, 0, 0, bitmap.width, bitmap.height, transform, true)
            val units = minOf(upright.width / 16, upright.height / 9)
            require(units > 0) { "Photo is too small" }
            val width = units * 16
            val height = units * 9
            cropped = Bitmap.createBitmap(upright, (upright.width - width) / 2, (upright.height - height) / 2, width, height)
            output = if (width > 1280) Bitmap.createScaledBitmap(cropped, 1280, 720, true) else cropped
            return ByteArrayOutputStream().use { out ->
                check(output.compress(Bitmap.CompressFormat.JPEG, 87, out)) { "Could not prepare photo" }
                out.toByteArray()
            }
        } finally {
            if (output !== cropped && output !== bitmap) output.recycle()
            if (cropped !== upright && cropped !== bitmap) cropped.recycle()
            if (upright !== bitmap) upright.recycle()
            bitmap.recycle()
        }
    }

    /** A picked image (content URI) is copied to the cache first so EXIF orientation can be read. */
    fun encodePhoto(context: Context, uri: Uri): ByteArray {
        val copy = File.createTempFile("practice-picked-", ".jpg", context.cacheDir)
        try {
            context.contentResolver.openInputStream(uri).use { input ->
                requireNotNull(input) { "No photo" }
                copy.outputStream().use { input.copyTo(it) }
            }
            return encodePhoto(copy)
        } finally { copy.delete() }
    }

    // --- portal ---------------------------------------------------------------------------------

    private fun open(context: Context, path: String, track: (HttpURLConnection?) -> Unit): Pair<HttpURLConnection, JSONObject> {
        val config = JSONObject(context.assets.open("visko.json").bufferedReader().use { it.readText() })
        val request = URL(config.getString("portalUrl").trimEnd('/') + path).openConnection() as HttpURLConnection
        track(request)
        request.instanceFollowRedirects = false
        request.connectTimeout = 7000
        config.optString("portalToken").takeIf { it.isNotBlank() }?.let { request.setRequestProperty("portal_token", it) }
        return request to config
    }

    private fun <T> call(context: Context, path: String, track: (HttpURLConnection?) -> Unit, block: (HttpURLConnection) -> T): T {
        val (request, _) = open(context, path, track)
        try {
            return block(request)
        } catch (e: java.io.IOException) {
            android.util.Log.w("PracticePortal", "$path failed", e)
            throw IllegalStateException(if (e is java.net.SocketTimeoutException) "The lesson service took too long. Try again."
                else "Could not reach the Portal. Check Wi-Fi and try again.")
        } finally {
            track(null)
            request.disconnect()
        }
    }

    private fun send(request: HttpURLConnection, contentType: String, body: ByteArray, readTimeout: Int) {
        request.requestMethod = "POST"
        request.readTimeout = readTimeout
        request.doOutput = true
        request.setRequestProperty("Content-Type", contentType)
        request.setFixedLengthStreamingMode(body.size)
        request.outputStream.use { it.write(body) }
    }

    private fun read(request: HttpURLConnection) = JSONObject(request.inputStream.bufferedReader().use { it.readText() })

    /** Same server transcription as the web app (OpenAI on the Portal; no key on the phone). */
    fun transcribe(context: Context, audio: ByteArray, language: String, track: (HttpURLConnection?) -> Unit = {}): String =
        call(context, "/api/agent/transcribe", track) { request ->
            if (language.isNotBlank()) request.setRequestProperty("x-audio-language", language) // blank: Portal auto-detects
            send(request, "audio/mp4", audio, 30000)
            if (request.responseCode != 200) throw IllegalStateException(when (request.responseCode) {
                429 -> "Speech service is busy. Wait a moment and try again."
                503 -> "Speech is not configured on the Portal. Type your goal instead."
                else -> "Could not understand the recording. Try again or type your goal."
            })
            val text = read(request).optString("text").trim()
            if (text.length < 2) throw IllegalStateException("No speech was detected. Hold the button and try again.")
            text.take(500)
        }

    /** Photo-grounded practice: the photo and goal go to the Portal, which keeps them up to 30 minutes. */
    fun createPractice(context: Context, image: ByteArray, goal: String, track: (HttpURLConnection?) -> Unit = {}): Practice =
        call(context, "/api/practice/generate", track) { request ->
            val body = JSONObject().put("image", "data:image/jpeg;base64," + Base64.encodeToString(image, Base64.NO_WRAP)).put("goal", goal)
            send(request, "application/json", body.toString().toByteArray(Charsets.UTF_8), 40000)
            val status = request.responseCode
            if (status != 200) throw IllegalStateException(when (status) {
                401 -> "The Portal rejected this app's access. Check the Portal configuration."
                429 -> "The Portal is busy. Wait a moment and try again."
                in 500..599 -> "The lesson service couldn't create a practice. Your photo is still here; try again."
                else -> "The Portal couldn't use this request. Check your photo and goal."
            })
            val result = read(request)
            val lesson = result.getJSONObject("lesson")
            Practice(lesson.getString("id"), lesson.getString("title"), image, result.getLong("expiresAt"))
        }

    /** Voice-only lesson written from the spoken goal (no photo); saved on the Portal as `gen-…`. */
    fun createScenario(context: Context, goal: String, level: String, language: String, track: (HttpURLConnection?) -> Unit = {}): JSONObject =
        call(context, "/api/scenarios", track) { request ->
            val body = JSONObject().put("request", goal).put("level", level)
            if (language.isNotBlank()) body.put("language", language)
            send(request, "application/json", body.toString().toByteArray(Charsets.UTF_8), 70000)
            val status = request.responseCode
            if (status != 201) throw IllegalStateException(when (status) {
                429 -> "A lesson is already being written. Wait a moment and try again."
                503 -> "Lesson writing is not configured on the Portal."
                else -> "Could not write a lesson from that. Try describing the situation again."
            })
            read(request)
        }
}
