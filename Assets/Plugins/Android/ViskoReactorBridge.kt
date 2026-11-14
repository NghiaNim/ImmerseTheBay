package com.immersethebay.visko

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Handler
import android.os.Looper
import inc.reactor.sdk.android.AudioFrame
import inc.reactor.sdk.android.ConnectionStatus
import inc.reactor.sdk.android.Reactor
import inc.reactor.sdk.android.ReactorOptions
import inc.reactor.sdk.android.VideoFrame
import java.nio.ByteBuffer
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

/**
 * Java-friendly wrapper around the Reactor Android SDK (Kotlin, coroutines) so Unity can drive it through
 * AndroidJavaObject / AndroidJavaProxy. One instance = one Reactor connection.
 *
 * Video: the latest main_video frame (BGRA) is copied into [frame]; Unity polls [getFrameId] and pulls pixels with
 * [copyFrameInto] / [getFrame]. Audio: main_audio is played natively with AudioTrack (no Unity audio path needed).
 */
class ViskoReactorBridge(private val listener: Listener) {

    /** Implemented in C# with AndroidJavaProxy. Called on the Android main thread. */
    interface Listener {
        fun onStatus(status: String)
        fun onSessionId(sessionId: String)
        fun onMessage(json: String)
        fun onOperation(id: String, command: String, json: String, error: String)
        fun onError(message: String)
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val main = Handler(Looper.getMainLooper())
    @Volatile private var reactor: Reactor? = null
    private var connectionJob: Job? = null
    private var connectionScope: CoroutineScope? = null
    private val commands = Mutex()
    private var disposed = false
    private val audioLock = Any()
    private var audio: AudioTrack? = null

    private val frameLock = Any()
    @Volatile private var frameId: Long = 0
    @Volatile private var width = 0
    @Volatile private var height = 0
    private var frame: ByteArray = ByteArray(0)

    @Volatile var audioEnabled: Boolean = true

    /** Creates a new session on [model] (sessionId empty) or joins an existing one. [jwt] must be bound to it. */
    fun connect(jwt: String, model: String, sessionId: String?) {
        main.post {
            if (disposed) return@post
            disconnectInternal()
            val job = SupervisorJob()
            val active = CoroutineScope(job + Dispatchers.Main.immediate)
            connectionJob = job
            connectionScope = active
            val r = Reactor(ReactorOptions(jwt = jwt), Dispatchers.Main.immediate)
            reactor = r
            active.launch { r.status.collect { s -> if (reactor === r) {
                listener.onStatus(s.name)
                if (s == ConnectionStatus.READY) attachTracks(r)
            } } }
            active.launch { r.sessionId.collect { id -> if (reactor === r && id != null) listener.onSessionId(id) } }
            active.launch { r.messages.collect { m -> if (reactor === r) listener.onMessage(toJson(m).toString()) } }
            active.launch { r.errors.collect { e -> if (reactor === r) listener.onError("${e.code}: ${e.message}") } }
            active.launch {
                try { if (sessionId.isNullOrEmpty()) r.connect(model) else r.connect(model, sessionId) }
                catch (e: CancellationException) { throw e }
                catch (e: Exception) { if (reactor === r) listener.onError("connect failed: ${e.message}") }
            }
        }
    }

    private var tracksAttached = false

    private fun attachTracks(r: Reactor) {
        if (tracksAttached) return
        tracksAttached = true
        try {
            r.track("main_video").onFrame { f: VideoFrame -> if (reactor === r) onVideo(r, f) }
        } catch (e: Exception) {
            main.post { listener.onError("main_video: ${e.message}") }
        }
        try {
            r.track("main_audio").onFrame { f: AudioFrame -> if (reactor === r) onAudio(r, f) }
        } catch (e: Exception) {
            main.post { listener.onError("main_audio: ${e.message}") }
        }
    }

    // Runs on the SDK media thread: copy and return quickly.
    private fun onVideo(r: Reactor, f: VideoFrame) {
        val n = f.width * f.height * 4
        synchronized(frameLock) {
            if (reactor !== r) return
            if (frame.size != n) frame = ByteArray(n)
            val buf = f.pixels.duplicate()
            buf.rewind()
            buf.get(frame, 0, minOf(n, buf.remaining()))
            width = f.width
            height = f.height
            frameId++
        }
    }

    private fun onAudio(r: Reactor, f: AudioFrame) = synchronized(audioLock) {
        if (!audioEnabled || reactor !== r) return@synchronized
        val track = audio ?: createAudioTrack(f.sampleRate, f.channels).also { audio = it }
        val count = f.sampleCount * f.channels
        val samples = ShortArray(count)
        val src = f.samples.duplicate()
        src.rewind()
        src.get(samples, 0, minOf(count, src.remaining()))
        track.write(samples, 0, samples.size, AudioTrack.WRITE_NON_BLOCKING)
    }

    private fun createAudioTrack(rate: Int, channels: Int): AudioTrack {
        val mask = if (channels == 2) AudioFormat.CHANNEL_OUT_STEREO else AudioFormat.CHANNEL_OUT_MONO
        val minBuf = AudioTrack.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT)
        return AudioTrack.Builder()
            .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_MOVIE).build())
            .setAudioFormat(AudioFormat.Builder().setSampleRate(rate).setChannelMask(mask).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
            .setBufferSizeInBytes(maxOf(minBuf * 4, rate)) // ~0.5-1 s of headroom for chunked audio
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build().also { it.play() }
    }

    // ------------------------------------------------------------ frame access for Unity
    fun getFrameId(): Long = frameId
    fun getWidth(): Int = width
    fun getHeight(): Int = height

    /** Returns a copy of the latest BGRA frame (width*height*4 bytes), or an empty array. */
    fun getFrame(): ByteArray = synchronized(frameLock) { frame.copyOf() }

    // ------------------------------------------------------------ commands
    /** Operations are serialized, including upload+set_image, and correlated by the caller's ID. */
    fun sendCommand(id: String, name: String, argsJson: String?) = operation(id, name) { r ->
        r.sendCommand(name, if (argsJson.isNullOrEmpty()) null else argsJson)
    }

    fun uploadImage(id: String, bytes: ByteArray, name: String, mime: String) = operation(id, "set_image") { r ->
        require(bytes.isNotEmpty() && bytes.size <= 16 * 1024 * 1024) { "Image must be between 1 byte and 16 MiB" }
        val buffer = ByteBuffer.allocateDirect(bytes.size)
        buffer.put(bytes)
        buffer.flip()
        val ref = r.uploadBytes(buffer, name, mime)
        val file = JSONObject().put("upload_id", ref.uploadId).put("name", ref.name)
            .put("mime_type", ref.mimeType).put("size", ref.size)
        val reply = r.sendCommand("set_image", null, JSONObject().put("image", file).toString())
        check(reply.type == "image_accepted") { "set_image did not return image_accepted: ${reply.type}" }
        reply
    }

    private fun operation(id: String, name: String,
        action: suspend (Reactor) -> inc.reactor.sdk.android.CommandReply) {
        main.post {
            val r = reactor
            val active = connectionScope
            if (disposed || r == null || active == null || r.status.value != ConnectionStatus.READY) {
                listener.onOperation(id, name, "", "Reactor is not READY")
                return@post
            }
            active.launch {
                try {
                    val reply = withTimeout(90000L) { commands.withLock {
                        check(reactor === r) { "Connection changed" }
                        action(r)
                    } }
                    check(reply.type != "command_error") { reply.data.toString() }
                    val expected = when (name) {
                        "set_prompt" -> "prompt_accepted"
                        "set_audio_prompt" -> "audio_prompt_accepted"
                        "set_image" -> "image_accepted"
                        "set_resolution" -> "resolution_accepted"
                        "set_audio_enabled" -> "audio_enabled_accepted"
                        "start" -> "generation_started"
                        "pause" -> "generation_paused"
                        "resume" -> "generation_resumed"
                        "reset" -> "generation_reset"
                        else -> null
                    }
                    check(expected == null || reply.type == expected) { "$name did not return $expected: ${reply.type}" }
                    if (reactor === r) listener.onOperation(id, name,
                        JSONObject().put("type", reply.type ?: "").put("data", wrap(reply.data)).toString(), "")
                } catch (e: Exception) {
                    if (reactor === r) listener.onOperation(id, name, "", e.message ?: "Operation cancelled")
                }
            }
        }
    }

    fun getSessionId(): String = reactor?.sessionId?.value ?: ""

    fun disconnect() { main.post { disconnectInternal() } }

    private fun disconnectInternal() {
        val r = reactor
        reactor = null
        connectionJob?.cancel()
        connectionJob = null
        connectionScope = null
        tracksAttached = false
        synchronized(frameLock) { frame = ByteArray(0); width = 0; height = 0; frameId++ }
        synchronized(audioLock) {
            audio?.let { try { it.stop() } catch (_: Exception) {}; it.release() }
            audio = null
        }
        if (r != null) scope.launch {
            try { withTimeout(5000L) { r.disconnect() } }
            catch (_: Exception) {}
            finally { r.close() }
        }
    }

    fun dispose() {
        main.post {
            if (!disposed) {
                disposed = true
                disconnectInternal()
                // disconnect cleanup runs on this scope; let its bounded call finish before cancelling.
                main.postDelayed({ scope.cancel() }, 6000)
            }
        }
    }

    // ------------------------------------------------------------ JSON helpers
    private fun toJson(map: Map<String, Any?>): JSONObject {
        val o = JSONObject()
        for ((k, v) in map) o.put(k, wrap(v))
        return o
    }

    private fun wrap(v: Any?): Any = when (v) {
        null -> JSONObject.NULL
        is Map<*, *> -> JSONObject().also { o -> v.forEach { (k, x) -> o.put(k.toString(), wrap(x)) } }
        is Collection<*> -> JSONArray().also { a -> v.forEach { a.put(wrap(it)) } }
        is Array<*> -> JSONArray().also { a -> v.forEach { a.put(wrap(it)) } }
        else -> v
    }
}
