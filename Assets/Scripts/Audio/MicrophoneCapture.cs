using System;
using System.Collections;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace ImmerseTheBay.Audio
{
    /// <summary>
    /// Records from the microphone (Beam Pro / phone, or the glasses' mic if Android routes it) into a looping clip
    /// and exposes the latest samples, RMS level and dB. Does not play the mic back, so there is no echo.
    /// </summary>
    public class MicrophoneCapture : MonoBehaviour
    {
        [Tooltip("Empty = system default microphone.")] public string deviceName = "";
        public int sampleRate = 48000;
        [Tooltip("PCM chunk duration in milliseconds; applied when capture starts.")]
        [Min(1)] public int chunkMs = 40;
        public bool startOnEnable = true;

        /// <summary>
        /// New signed 16-bit mono samples, emitted on Unity's main thread. Each array can be retained
        /// by a network consumer; subscribers must not mutate it. StopCapture flushes a final short chunk.
        /// </summary>
        public event Action<short[], int> OnPcmChunk;

        public bool IsCaptureRequested => m_Starting || IsRecording;
        public bool IsRecording => m_Clip && Microphone.IsRecording(m_Device);
        public float Rms { get; private set; }
        public float Db { get; private set; } = -80f;
        public float Peak { get; private set; }
        public AudioClip Clip => m_Clip;
        public string Status { get; private set; } = "idle";

        AudioClip m_Clip;
        // Allow ordinary frame hitches without the native writer overwriting unread data.
        const int ClipSeconds = 10;
        string m_Device;
        bool m_Starting, m_Stopping, m_EmittingPcm, m_StopRequested;
        PcmChunkReader m_Pcm;
        Func<float[], int, bool> m_ReadPcm;
        double m_LastPcmPoll;
        float[] m_LatestInterleaved;
        int m_PcmOverruns;
        long m_PcmFrames;
        readonly float[] m_Window = new float[1024];
        Func<string> m_Debug;

        void Awake()
        {
            m_Debug = () => $"<b>== MICROPHONE ==</b>\n{Status}\ndevices: {string.Join(", ", Microphone.devices)}" +
                            (IsRecording ? $"\nRMS {Rms:F3}  {Db:F1} dB  peak {Peak:F2}" +
                                $"\nPCM {chunkMs}ms mono16, frames={m_PcmFrames}, overruns={m_PcmOverruns}" : "");
        }

        void OnEnable()
        {
            DebugRegistry.Register(m_Debug);
            if (startOnEnable) StartCapture();
        }

        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            StopCapture();
        }

        public void StartCapture()
        {
            if (!isActiveAndEnabled || m_Starting || m_Stopping || m_Clip) return;
            m_Starting = true;
            StartCoroutine(StartRoutine());
        }

        IEnumerator StartRoutine()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Status = "requesting RECORD_AUDIO permission...";
                Permission.RequestUserPermission(Permission.Microphone);
                float t = 0;
                while (!Permission.HasUserAuthorizedPermission(Permission.Microphone) && t < 15f) { t += Time.unscaledDeltaTime; yield return null; }
                if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
                { m_Starting = false; Status = "<color=#f88>microphone permission denied</color>"; yield break; }
            }
#endif
            if (Microphone.devices.Length == 0)
            { m_Starting = false; Status = "<color=#f88>no microphone found</color>"; yield break; }
            m_Device = string.IsNullOrEmpty(deviceName) ? null : deviceName;
            Microphone.GetDeviceCaps(m_Device, out int minF, out int maxF);
            int rate = (minF == 0 && maxF == 0) ? Mathf.Max(1, sampleRate) : Mathf.Clamp(sampleRate, Mathf.Max(1, minF), maxF);
            m_Clip = Microphone.Start(m_Device, true, ClipSeconds, rate);
            if (!m_Clip) { m_Starting = false; Status = "<color=#f88>microphone failed to start</color>"; yield break; }
            ConfigurePcm(m_Clip);
            m_LastPcmPoll = Time.realtimeSinceStartupAsDouble;
            m_PcmOverruns = 0;
            m_PcmFrames = 0;
            Status = "starting microphone...";
            float wait = 0;
            while (Microphone.GetPosition(m_Device) <= 0 && wait < 3f) { wait += Time.unscaledDeltaTime; yield return null; }
            m_Starting = false;
            if (Microphone.GetPosition(m_Device) <= 0)
            { StopCapture(); Status = "<color=#f88>microphone start timed out</color>"; yield break; }
            Status = $"recording '{m_Device ?? Microphone.devices[0]}' @ {m_Clip.frequency} Hz";
        }

        public void StopCapture()
        {
            if (m_EmittingPcm) { m_StopRequested = true; return; }
            if (m_Stopping) return;
            m_Stopping = true;
            StopAllCoroutines();
            m_Starting = false;
            if (m_Clip)
            {
                if (IsRecording)
                {
                    PollPcm();
                    if (m_Pcm != null && m_Pcm.TryReadChunk(m_ReadPcm, out var tail, true)) EmitPcm(tail, m_Pcm.SampleRate);
                }
                Microphone.End(m_Device);
                Destroy(m_Clip);
            }
            m_Clip = null;
            m_Pcm = null;
            m_ReadPcm = null;
            m_LatestInterleaved = null;
            Rms = Peak = 0;
            Db = -80f;
            Status = "stopped";
            m_Stopping = false;
        }

        /// <summary>Copies the most recent <paramref name="buffer"/>.Length mono samples. Returns false if not recording.</summary>
        public bool GetLatestSamples(float[] buffer)
        {
            if (!IsRecording || buffer == null || buffer.Length == 0 || buffer.Length > m_Clip.samples) return false;
            int position = Microphone.GetPosition(m_Device);
            if (position < 0) return false;
            int pos = (position - buffer.Length + m_Clip.samples) % m_Clip.samples;
            if (m_Clip.channels == 1) return m_Clip.GetData(buffer, pos);
            int channels = m_Clip.channels;
            if (m_LatestInterleaved == null || m_LatestInterleaved.Length != buffer.Length * channels)
                m_LatestInterleaved = new float[buffer.Length * channels];
            if (!m_Clip.GetData(m_LatestInterleaved, pos)) return false;
            for (int frame = 0; frame < buffer.Length; frame++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++) sum += m_LatestInterleaved[frame * channels + channel];
                buffer[frame] = sum / channels;
            }
            return true;
        }

        // Shared with the editor-only synthetic-clip check; no microphone/device is needed there.
        internal void ConfigurePcm(AudioClip clip)
        {
            m_Pcm = new PcmChunkReader(clip.samples, clip.channels, clip.frequency, chunkMs);
            m_ReadPcm = clip.GetData;
        }

        internal void DrainPcm(int writePosition)
        {
            var reader = m_Pcm;
            if (reader == null || writePosition < 0) return;
            if (!reader.AdvanceTo(writePosition)) m_PcmOverruns++;
            while (m_Pcm == reader && reader.TryReadChunk(m_ReadPcm, out var pcm)) EmitPcm(pcm, reader.SampleRate);
        }

        void PollPcm()
        {
            int position = Microphone.GetPosition(m_Device);
            if (position < 0 || m_Pcm == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - m_LastPcmPoll >= (double)m_Clip.samples / m_Clip.frequency)
            {
                // A full lap is ambiguous from the modulo cursor. Report loss instead of replaying stale samples.
                m_Pcm.Resync(position);
                m_PcmOverruns++;
                Debug.LogWarning("[Microphone] PCM ring overrun after a main-thread stall; resynchronised at writer.", this);
            }
            m_LastPcmPoll = now;
            DrainPcm(position);
        }

        void EmitPcm(short[] pcm, int rate)
        {
            m_PcmFrames += pcm.Length;
            var subscribers = OnPcmChunk;
            if (subscribers == null) return;
            m_EmittingPcm = true;
            foreach (Action<short[], int> subscriber in subscribers.GetInvocationList())
            {
                try { subscriber(pcm, rate); }
                catch (Exception error) { Debug.LogException(error, this); }
            }
            m_EmittingPcm = false;
            if (m_StopRequested) { m_StopRequested = false; StopCapture(); }
        }

        void Update()
        {
            if (IsRecording) PollPcm();
            if (!GetLatestSamples(m_Window)) return;
            float sum = 0, peak = 0;
            foreach (var s in m_Window) { sum += s * s; peak = Mathf.Max(peak, Mathf.Abs(s)); }
            Rms = Mathf.Sqrt(sum / m_Window.Length);
            Db = 20f * Mathf.Log10(Mathf.Max(Rms, 1e-4f));
            Peak = peak;
        }
    }
}
