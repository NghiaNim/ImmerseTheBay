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
        fun onReply(command: String, json: String)
        fun onError(message: String)
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val main = Handler(Looper.getMainLooper())
    private var reactor: Reactor? = null
    private var audio: AudioTrack? = null

    private val frameLock = Any()
    @Volatile private var frameId: Long = 0
    @Volatile private var width = 0
    @Volatile private var height = 0
    private var frame: ByteArray = ByteArray(0)

    @Volatile var audioEnabled: Boolean = true

    /** Creates a new session on [model] (sessionId empty) or joins an existing one. [jwt] must be bound to it. */
    fun connect(jwt: String, model: String, sessionId: String?) {
        disconnect()
        val r = Reactor(ReactorOptions(jwt = jwt), Dispatchers.Main.immediate)
        reactor = r

        scope.launch { r.status.collect { s -> listener.onStatus(s.name); if (s == ConnectionStatus.READY) attachTracks(r) } }
        scope.launch { r.sessionId.collect { id -> if (id != null) listener.onSessionId(id) } }
        scope.launch { r.messages.collect { m -> listener.onMessage(toJson(m).toString()) } }
        scope.launch { r.errors.collect { e -> listener.onError("${e.code}: ${e.message}") } }

        scope.launch {
            try {
                if (sessionId.isNullOrEmpty()) r.connect(model) else r.connect(model, sessionId)
            } catch (e: Exception) {
                listener.onError("connect failed: ${e.message}")
            }
        }
    }

    private var tracksAttached = false

    private fun attachTracks(r: Reactor) {
        if (tracksAttached) return
        tracksAttached = true
        try {
            r.track("main_video").onFrame { f: VideoFrame -> onVideo(f) }
        } catch (e: Exception) {
            main.post { listener.onError("main_video: ${e.message}") }
        }
        try {
            r.track("main_audio").onFrame { f: AudioFrame -> onAudio(f) }
        } catch (e: Exception) {
            main.post { listener.onError("main_audio: ${e.message}") }
        }
    }

    // Runs on the SDK media thread: copy and return quickly.
    private fun onVideo(f: VideoFrame) {
        val n = f.width * f.height * 4
        synchronized(frameLock) {
            if (frame.size != n) frame = ByteArray(n)
            val buf = f.pixels.duplicate()
            buf.rewind()
            buf.get(frame, 0, minOf(n, buf.remaining()))
            width = f.width
            height = f.height
            frameId++
        }
    }

    private fun onAudio(f: AudioFrame) {
        if (!audioEnabled) return
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
    /** [argsJson] is a JSON object string or empty. The reply (if any) arrives via Listener.onReply. */
    fun sendCommand(name: String, argsJson: String?) {
        val r = reactor ?: run { listener.onError("sendCommand($name): not connected"); return }
        scope.launch {
            try {
                val reply = r.sendCommand(name, if (argsJson.isNullOrEmpty()) null else argsJson)
                val json = JSONObject().put("type", reply.type ?: "").put("data", wrap(reply.data))
                listener.onReply(name, json.toString())
            } catch (e: Exception) {
                listener.onError("command $name failed: ${e.message}")
            }
        }
    }

    fun getSessionId(): String = reactor?.sessionId?.value ?: ""

    fun disconnect() {
        val r = reactor ?: return
        reactor = null
        tracksAttached = false
        scope.launch {
            try { r.disconnect() } catch (_: Exception) {}
            r.close()
        }
        audio?.let { try { it.stop(); it.release() } catch (_: Exception) {} }
        audio = null
    }

    fun dispose() {
        disconnect()
        main.postDelayed({ scope.cancel() }, 2000)
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
