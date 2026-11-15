using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ImmerseTheBay.Audio;
using ImmerseTheBay.Visko;
using UnityEngine;
using UnityEngine.Networking;

namespace ImmerseTheBay.Learning
{
    /// <summary>
    /// Hands-free utterance recognition through the portal's server transcription (/api/agent/transcribe).
    /// One request waits for the learner to start speaking, ends the utterance after a short pause and returns
    /// the transcript. Unlike Android's recognizer there are no start/stop tones and no 3-second window, so the
    /// learner can take time to read the line. The scene's audio is muted while listening because the glasses'
    /// microphone sits next to their speakers.
    /// </summary>
    public sealed class PortalSpeechToText : MonoBehaviour, ISpeechToText
    {
        [Tooltip("Stop waiting when nobody starts speaking within this many seconds.")]
        public float waitForSpeechSeconds = 20f;
        [Tooltip("A pause this long ends the utterance.")]
        public float endSilenceSeconds = 0.9f;
        [Tooltip("Longest utterance sent for transcription.")]
        public float maxUtteranceSeconds = 15f;
        [Tooltip("Transcription language hint (ja, zh or en), as the web app sends for answers. Empty lets the server detect it.")]
        public string languageHint = "ja";

        public event Action<string> PartialResult;
        public event Action<string> FinalResult;
        public event Action<string> Error;
        /// <summary>True from StartListening until the final result or error, including transcription.</summary>
        public bool IsListening { get; private set; }
        public bool IsCapturing => IsListening && !m_Transcribing && m_Microphone && m_Microphone.IsRecording;
        public float InputLevel => IsCapturing
            ? Mathf.InverseLerp(Mathf.Max(0.008f, m_Floor), 0.16f, m_Microphone.Rms)
            : 0f;
        public string Status { get; private set; } = "idle";

        const int SampleRate = 16000;
        const float PreRollSeconds = 0.3f;
        // Normalized RMS. Speech must clear 3x the room's noise level (2x to keep going) and these minimums.
        const float MinStartLevel = 0.015f, MinEndLevel = 0.008f;
        const int StartChunks = 3; // ~120 ms of sound before it counts as speech

        MicrophoneCapture m_Microphone;
        ViskoSession m_Scene;
        readonly List<MicrophoneCapture> m_Paused = new();
        readonly Queue<short[]> m_PreRoll = new();
        readonly List<short[]> m_Utterance = new();
        int m_PreRollSamples, m_UtteranceSamples, m_Rate, m_LoudChunks, m_Request;
        long m_HeardSamples, m_LastVoiceSample;
        float m_Floor, m_StartedAt;
        bool m_Speaking, m_Transcribing, m_Muted, m_RestorePending;
        UnityWebRequest m_Upload;
        Func<string> m_Debug;

        [Serializable] sealed class Transcript { public string text; }

        void Awake()
        {
            m_Debug = () => $"<b>== PORTAL SPEECH ==</b>\n{Status}" + (m_Microphone ? $"\nmic {m_Microphone.Status}" : "");
            var child = new GameObject("Portal speech microphone");
            child.SetActive(false);
            child.transform.SetParent(transform, false);
            m_Microphone = child.AddComponent<MicrophoneCapture>();
            m_Microphone.startOnEnable = false;
            m_Microphone.sampleRate = SampleRate;
            m_Microphone.chunkMs = 40;
            child.SetActive(true);
            m_Microphone.OnPcmChunk += OnPcm;
        }

        void OnEnable() => DebugRegistry.Register(m_Debug);
        void OnDisable() { DebugRegistry.Unregister(m_Debug); Dispose(); }
        void OnApplicationPause(bool paused) { if (paused) Dispose(); }
        void OnDestroy()
        {
            if (m_Microphone) { m_Microphone.OnPcmChunk -= OnPcm; Destroy(m_Microphone.gameObject); }
        }

        void Update()
        {
            // Restart microphone users paused for this request only once our capture has really stopped.
            if (m_RestorePending && !m_Microphone.IsCaptureRequested) RestorePaused();
            // A denied permission or missing microphone makes MicrophoneCapture give up without an event.
            if (IsListening && !m_Transcribing && !m_Microphone.IsCaptureRequested && Time.realtimeSinceStartup - m_StartedAt > 1f)
                StopWithoutTranscript("Microphone unavailable: " + Regex.Replace(m_Microphone.Status, "<[^>]+>", ""));
        }

        public void StartListening()
        {
            if (IsListening || !isActiveAndEnabled) return;
            IsListening = true;
            m_Request++;
            m_Speaking = m_Transcribing = false;
            m_PreRoll.Clear();
            m_Utterance.Clear();
            m_PreRollSamples = m_UtteranceSamples = m_LoudChunks = 0;
            m_HeardSamples = m_LastVoiceSample = 0;
            m_Floor = -1;
            m_Rate = SampleRate;
            m_StartedAt = Time.realtimeSinceStartup;
            // One capture at a time: pause other microphone users until this request ends.
            m_RestorePending = false;
            foreach (var capture in FindObjectsByType<MicrophoneCapture>(FindObjectsSortMode.None))
                if (capture != m_Microphone && capture.IsCaptureRequested) { m_Paused.Add(capture); capture.StopCapture(); }
            MuteScene(true);
            if (m_Microphone.IsCaptureRequested) m_Microphone.StopCapture(); // leftover from the previous request
            m_Microphone.StartCapture();
            Status = "waiting for speech";
        }

        /// <summary>Finish now: transcribe what was said so far, or report that nothing was said.</summary>
        public void StopListening()
        {
            if (!IsListening || m_Transcribing) return;
            if (m_Speaking && m_UtteranceSamples > 0) Transcribe(m_Request);
            else StopWithoutTranscript("No speech heard");
        }

        public void Dispose()
        {
            m_Request++;
            m_Upload?.Abort();
            m_Upload = null;
            ReleaseMicrophone();
            RestorePaused();
            IsListening = m_Speaking = m_Transcribing = false;
            m_PreRoll.Clear();
            m_Utterance.Clear();
            Status = "stopped";
        }

        void OnPcm(short[] pcm, int rate)
        {
            if (!IsListening || m_Transcribing || pcm.Length == 0) return;
            m_Rate = rate;
            double sum = 0;
            foreach (var sample in pcm) sum += (double)sample * sample;
            float level = (float)(Math.Sqrt(sum / pcm.Length) / 32768.0);
            m_HeardSamples += pcm.Length;

            if (!m_Speaking)
            {
                // The room's noise level follows quiet moments quickly and loud ones slowly.
                m_Floor = m_Floor < 0 ? Mathf.Min(level, 0.01f) : Mathf.Lerp(m_Floor, level, level < m_Floor ? 0.3f : 0.02f);
                m_PreRoll.Enqueue(pcm);
                m_PreRollSamples += pcm.Length;
                while (m_PreRoll.Count > 1 && m_PreRollSamples - m_PreRoll.Peek().Length >= PreRollSeconds * rate)
                    m_PreRollSamples -= m_PreRoll.Dequeue().Length;
                m_LoudChunks = level > Mathf.Max(m_Floor * 3f, MinStartLevel) ? m_LoudChunks + 1 : 0;
                if (m_LoudChunks >= StartChunks)
                {
                    // Keep the moment before the threshold so the first syllable is not cut off.
                    m_Speaking = true;
                    foreach (var chunk in m_PreRoll) { m_Utterance.Add(chunk); m_UtteranceSamples += chunk.Length; }
                    m_PreRoll.Clear();
                    m_PreRollSamples = 0;
                    m_LastVoiceSample = m_HeardSamples;
                    Status = "hearing speech";
                    PartialResult?.Invoke("Hearing you…");
                }
                else if (m_HeardSamples >= waitForSpeechSeconds * rate) StopWithoutTranscript("No speech heard");
                return;
            }

            m_Utterance.Add(pcm);
            m_UtteranceSamples += pcm.Length;
            if (level > Mathf.Max(m_Floor * 2f, MinEndLevel)) m_LastVoiceSample = m_HeardSamples;
            if (m_HeardSamples - m_LastVoiceSample >= endSilenceSeconds * rate || m_UtteranceSamples >= maxUtteranceSeconds * rate)
                Transcribe(m_Request);
        }

        async void Transcribe(int request)
        {
            m_Transcribing = true;
            ReleaseMicrophone();
            Status = "transcribing";
            PartialResult?.Invoke("Checking what you said…");
            var wav = EncodeWav(m_Utterance, m_UtteranceSamples, m_Rate);
            m_Utterance.Clear();
            string text = null, error = null;
            try
            {
                if (!m_Scene) m_Scene = FindAnyObjectByType<ViskoSession>();
                if (!m_Scene) throw new InvalidOperationException("portal settings are unavailable");
                await m_Scene.ConfigReady;
                if (request != m_Request) return;
                using var upload = new UnityWebRequest(m_Scene.config.portalUrl.TrimEnd('/') + "/api/agent/transcribe", "POST");
                upload.uploadHandler = new UploadHandlerRaw(wav);
                upload.downloadHandler = new DownloadHandlerBuffer();
                upload.SetRequestHeader("Content-Type", "audio/wav");
                if (!string.IsNullOrEmpty(languageHint)) upload.SetRequestHeader("x-audio-language", languageHint);
                if (!string.IsNullOrEmpty(m_Scene.config.portalToken)) upload.SetRequestHeader("portal_token", m_Scene.config.portalToken);
                upload.timeout = 25;
                m_Upload = upload;
                var operation = upload.SendWebRequest();
                while (!operation.isDone) await Task.Yield();
                if (request != m_Request) return;
                m_Upload = null;
                if (upload.result == UnityWebRequest.Result.Success)
                    text = JsonUtility.FromJson<Transcript>(upload.downloadHandler.text)?.text?.Trim();
                else error = upload.responseCode switch
                {
                    429 => "Speech service is busy. Try again in a moment.",
                    503 => "Server transcription is not configured on the portal.",
                    0 => "Could not reach the portal for speech.",
                    _ => $"Speech could not be transcribed ({upload.responseCode}).",
                };
            }
            catch (Exception e) { error = "Speech service unavailable: " + e.Message; }
            if (request != m_Request) return;
            Finish(text, error);
        }

        void StopWithoutTranscript(string error)
        {
            ReleaseMicrophone();
            Finish(null, error);
        }

        void Finish(string text, string error)
        {
            IsListening = m_Speaking = m_Transcribing = false;
            m_PreRoll.Clear();
            m_Utterance.Clear();
            if (error == null && string.IsNullOrEmpty(text)) error = "No speech was recognized";
            Status = error ?? "ready";
            if (error != null) Error?.Invoke(error);
            else FinalResult?.Invoke(text);
        }

        // Stopping can be deferred while a PCM chunk is being delivered, so paused users restart later (Update).
        void ReleaseMicrophone()
        {
            if (m_Microphone) m_Microphone.StopCapture();
            m_RestorePending = m_Paused.Count > 0;
            MuteScene(false);
        }

        void RestorePaused()
        {
            m_RestorePending = false;
            foreach (var capture in m_Paused) if (capture) capture.StartCapture();
            m_Paused.Clear();
        }

        void MuteScene(bool muted)
        {
            if (m_Muted == muted) return;
            m_Muted = muted;
            if (!m_Scene) m_Scene = FindAnyObjectByType<ViskoSession>();
            if (m_Scene) m_Scene.SetSceneAudioMuted(muted);
        }

        /// <summary>16-bit mono PCM WAV, the format /api/agent/transcribe accepts as audio/wav.</summary>
        static byte[] EncodeWav(List<short[]> chunks, int samples, int rate)
        {
            var wav = new byte[44 + samples * 2];
            void Tag(int at, string tag) { for (int i = 0; i < 4; i++) wav[at + i] = (byte)tag[i]; }
            void Int32(int at, int value) { for (int i = 0; i < 4; i++) wav[at + i] = (byte)(value >> (8 * i)); }
            void Int16(int at, int value) { wav[at] = (byte)value; wav[at + 1] = (byte)(value >> 8); }
            Tag(0, "RIFF"); Int32(4, 36 + samples * 2); Tag(8, "WAVE");
            Tag(12, "fmt "); Int32(16, 16); Int16(20, 1); Int16(22, 1); Int32(24, rate); Int32(28, rate * 2); Int16(32, 2); Int16(34, 16);
            Tag(36, "data"); Int32(40, samples * 2);
            int offset = 44;
            foreach (var chunk in chunks)
                foreach (var sample in chunk) { wav[offset++] = (byte)sample; wav[offset++] = (byte)(sample >> 8); }
            return wav;
        }
    }
}
