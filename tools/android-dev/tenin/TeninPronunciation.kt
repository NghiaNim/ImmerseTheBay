package com.immersethebay.phone

import android.content.Context
import android.media.AudioAttributes
import android.media.MediaDataSource
import android.media.MediaPlayer
import android.os.Handler
import org.json.JSONObject
import java.net.HttpURLConnection
import java.util.concurrent.CancellationException
import java.util.concurrent.Executors

/** One playback owns the clip, its UI callback and any pending Portal request. Called on main. */
internal class TeninPronunciation(private val context: Context, private val main: Handler) {
    private data class Cue(val span: IntRange, val from: Int, val to: Int)

    private val worker = Executors.newSingleThreadExecutor()
    private val disconnectWorker = Executors.newSingleThreadExecutor()
    private val requestLock = Any()
    private var generation = 0
    private var connection: HttpURLConnection? = null
    private var player: MediaPlayer? = null
    private var update: ((IntRange?) -> Unit)? = null
    private var ticker: Runnable? = null

    fun play(text: String, slow: Boolean, wholeWord: Boolean, onSpan: (IntRange?) -> Unit, onUnavailable: () -> Unit) {
        stop()
        val spoken = text.trim()
        if (spoken.isEmpty()) return
        val offset = text.indexOf(spoken)
        val run = synchronized(requestLock) { generation }
        update = onSpan
        worker.execute {
            if (!current(run)) return@execute
            val result = runCatching {
                PracticePortal.pronunciation(context, spoken, slow) { request ->
                    val accepted = synchronized(requestLock) {
                        (generation == run).also { if (it) connection = request }
                    }
                    if (!accepted && request != null) {
                        request.disconnect()
                        throw CancellationException()
                    }
                }
            }
            val cues = readCues(result.getOrNull()?.ranges, spoken.length, offset)
            main.post {
                if (!current(run)) return@post
                val clip = result.getOrNull()
                if (clip == null) {
                    stop()
                    onUnavailable()
                } else begin(clip, cues, if (wholeWord) offset until (offset + spoken.length) else null, onUnavailable)
            }
        }
    }

    private fun current(run: Int) = synchronized(requestLock) { generation == run }

    private fun begin(clip: PracticePortal.Pronunciation, cues: List<Cue>, word: IntRange?, onUnavailable: () -> Unit) {
        val audio = MediaPlayer()
        player = audio
        fun fail() {
            if (player !== audio) return
            stop()
            onUnavailable()
        }
        try {
            audio.setAudioAttributes(AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_MEDIA)
                .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
            val bytes = clip.wav
            audio.setDataSource(object : MediaDataSource() {
                override fun getSize(): Long = bytes.size.toLong()
                override fun readAt(position: Long, buffer: ByteArray, offset: Int, size: Int): Int {
                    if (size == 0) return 0
                    if (position < 0 || position >= bytes.size) return -1
                    val count = minOf(size, bytes.size - position.toInt())
                    bytes.copyInto(buffer, offset, position.toInt(), position.toInt() + count)
                    return count
                }
                override fun close() = Unit
            })
            audio.setOnErrorListener { _, _, _ -> fail(); true }
            audio.setOnCompletionListener { if (player === audio) stop() }
            audio.setOnPreparedListener {
                if (player !== audio) return@setOnPreparedListener
                try {
                    audio.start()
                    if (clip.slowAtPlayback) audio.playbackParams = audio.playbackParams.setSpeed(0.8f)
                    if (word != null) update?.invoke(word) else track(audio, cues)
                } catch (_: IllegalStateException) { fail() }
            }
            audio.prepareAsync()
        } catch (_: Exception) { fail() }
    }

    private fun track(audio: MediaPlayer, cues: List<Cue>) {
        if (cues.isEmpty()) return
        ticker = object : Runnable {
            private var previous: IntRange? = null
            override fun run() {
                if (player !== audio) return
                val position = audio.currentPosition
                val span = cues.firstOrNull { position >= it.from && position < it.to }?.span
                if (span != previous) {
                    previous = span
                    update?.invoke(span)
                }
                main.postDelayed(this, 25)
            }
        }.also { main.post(it) }
    }

    private fun readCues(header: String?, textLength: Int, offset: Int): List<Cue> {
        if (header == null || header.length > 16384) return emptyList()
        return runCatching {
            val ranges = JSONObject(header).getJSONArray("ranges")
            check(ranges.length() <= textLength)
            var lastEnd = 0
            var lastTime = 0
            List(ranges.length()) { index ->
                val item = ranges.getJSONObject(index)
                val start = item.getInt("start")
                val end = item.getInt("end")
                val from = item.getInt("from")
                val to = item.getInt("to")
                check(start >= lastEnd && end in (start + 1)..textLength && from >= lastTime && from < to && to <= 120_000)
                lastEnd = end
                lastTime = to
                Cue((start + offset) until (end + offset), from, to)
            }
        }.getOrDefault(emptyList())
    }

    fun stop() {
        val active = synchronized(requestLock) {
            generation++
            connection.also { connection = null }
        }
        if (active != null) disconnectWorker.execute { active.disconnect() }
        ticker?.let(main::removeCallbacks)
        ticker = null
        player?.release()
        player = null
        val clear = update
        update = null
        clear?.invoke(null)
    }

    fun close() {
        stop()
        worker.shutdownNow()
        disconnectWorker.shutdown()
    }
}
