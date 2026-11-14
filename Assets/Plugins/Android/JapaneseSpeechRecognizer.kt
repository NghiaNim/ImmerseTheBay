package com.immersethebay.visko

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer

/** One utterance at a time. Every recognizer operation runs on Android's application thread. */
class JapaneseSpeechRecognizer(context: Context, private val listener: Listener) {
    interface Listener {
        fun onPartial(requestId: Int, text: String)
        // Emitted only after cancel/destroy releases this request's recognizer.
        fun onFinished(requestId: Int, text: String, error: String)
    }

    private val context = context.applicationContext
    private val main = Handler(Looper.getMainLooper())
    private var recognizer: SpeechRecognizer? = null
    private var activeId: Int? = null
    private var disposed = false
    private var timeout: Runnable? = null

    fun startListening(requestId: Int) = main.post {
        if (disposed) {
            listener.onFinished(requestId, "", "Speech recognizer is disposed")
        } else if (activeId != null) {
            listener.onFinished(requestId, "", "Speech recognition is busy")
        } else {
            activeId = requestId
            try {
                if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                    finish(requestId, "", "Microphone permission is required")
                } else if (!SpeechRecognizer.isRecognitionAvailable(context)) {
                    finish(requestId, "", "No speech recognition service is available")
                } else {
                    val speech = SpeechRecognizer.createSpeechRecognizer(context)
                    recognizer = speech
                    speech.setRecognitionListener(object : RecognitionListener {
                        override fun onReadyForSpeech(params: Bundle?) {}
                        override fun onBeginningOfSpeech() {}
                        override fun onRmsChanged(rmsdB: Float) {}
                        override fun onBufferReceived(buffer: ByteArray?) {}
                        override fun onEndOfSpeech() {}
                        override fun onEvent(eventType: Int, params: Bundle?) {}
                        override fun onPartialResults(results: Bundle?) {
                            if (activeId != requestId) return
                            val text = results?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()
                            if (!text.isNullOrEmpty()) listener.onPartial(requestId, text)
                        }
                        override fun onResults(results: Bundle?) {
                            val text = results?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull().orEmpty()
                            finish(requestId, text, if (text.isEmpty()) "No speech was recognized" else "")
                        }
                        override fun onError(error: Int) = finish(requestId, "", "Speech recognition error $error")
                    })
                    speech.startListening(Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
                        putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                        putExtra(RecognizerIntent.EXTRA_LANGUAGE, "ja-JP")
                        putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
                        putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1)
                    })
                    // Some providers fail to deliver a terminal callback. Never hold the mic indefinitely.
                    timeout = Runnable { finish(requestId, "", "Speech recognition timed out") }
                    main.postDelayed(timeout!!, 45000)
                }
            } catch (error: Exception) {
                finish(requestId, "", "Speech recognition failed: ${error.message}")
            }
        }
    }

    fun stopListening(requestId: Int) = main.post {
        if (activeId == requestId) {
            try { recognizer?.stopListening() }
            catch (error: Exception) { finish(requestId, "", "Could not stop speech recognition: ${error.message}") }
            // Wait for onResults/onError before allowing a new utterance.
        }
    }

    fun dispose(requestId: Int) = main.post {
        disposed = true
        val id = activeId
        if (id != null) finish(id, "", "")
        else listener.onFinished(requestId, "", "")
    }

    private fun finish(requestId: Int, text: String, error: String) {
        if (activeId != requestId) return
        activeId = null // Reject synchronous or delayed callbacks caused by cancellation.
        timeout?.let { main.removeCallbacks(it) }
        timeout = null
        val speech = recognizer
        recognizer = null
        try { speech?.cancel() } catch (_: Exception) {}
        try { speech?.destroy() } catch (_: Exception) {}
        listener.onFinished(requestId, text, error)
    }
}
