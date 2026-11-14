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
        public bool startOnEnable = true;

        public bool IsRecording => m_Clip && Microphone.IsRecording(m_Device);
        public float Rms { get; private set; }
        public float Db { get; private set; } = -80f;
        public float Peak { get; private set; }
        public AudioClip Clip => m_Clip;
        public string Status { get; private set; } = "idle";

        AudioClip m_Clip;
        string m_Device;
        readonly float[] m_Window = new float[1024];
        Func<string> m_Debug;

        void Awake()
        {
            m_Debug = () => $"<b>== MICROPHONE ==</b>\n{Status}\ndevices: {string.Join(", ", Microphone.devices)}" +
                            (IsRecording ? $"\nRMS {Rms:F3}  {Db:F1} dB  peak {Peak:F2}" : "");
        }

        void OnEnable()
        {
            DebugRegistry.Register(m_Debug);
            if (startOnEnable) StartCoroutine(StartRoutine());
        }

        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            StopCapture();
        }

        public void StartCapture() => StartCoroutine(StartRoutine());

        IEnumerator StartRoutine()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Status = "requesting RECORD_AUDIO permission...";
                Permission.RequestUserPermission(Permission.Microphone);
                float t = 0;
                while (!Permission.HasUserAuthorizedPermission(Permission.Microphone) && t < 15f) { t += Time.unscaledDeltaTime; yield return null; }
                if (!Permission.HasUserAuthorizedPermission(Permission.Microphone)) { Status = "<color=#f88>microphone permission denied</color>"; yield break; }
            }
#endif
            if (Microphone.devices.Length == 0) { Status = "<color=#f88>no microphone found</color>"; yield break; }
            m_Device = string.IsNullOrEmpty(deviceName) ? null : deviceName;
            Microphone.GetDeviceCaps(m_Device, out int minF, out int maxF);
            int rate = (minF == 0 && maxF == 0) ? sampleRate : Mathf.Clamp(sampleRate, minF, maxF);
            m_Clip = Microphone.Start(m_Device, true, 1, rate);
            float wait = 0;
            while (Microphone.GetPosition(m_Device) <= 0 && wait < 3f) { wait += Time.unscaledDeltaTime; yield return null; }
            Status = $"recording '{m_Device ?? Microphone.devices[0]}' @ {rate} Hz";
        }

        public void StopCapture()
        {
            if (m_Clip) Microphone.End(m_Device);
            m_Clip = null;
            Status = "stopped";
        }

        /// <summary>Copies the most recent <paramref name="buffer"/>.Length mono samples. Returns false if not recording.</summary>
        public bool GetLatestSamples(float[] buffer)
        {
            if (!IsRecording) return false;
            int pos = Microphone.GetPosition(m_Device) - buffer.Length;
            if (pos < 0) pos += m_Clip.samples;
            m_Clip.GetData(buffer, pos); // GetData wraps around the looping clip
            return true;
        }

        void Update()
        {
            if (!GetLatestSamples(m_Window)) return;
            float sum = 0, peak = 0;
            foreach (var s in m_Window) { sum += s * s; peak = Mathf.Max(peak, Mathf.Abs(s)); }
            Rms = Mathf.Sqrt(sum / m_Window.Length);
            Db = 20f * Mathf.Log10(Mathf.Max(Rms, 1e-4f));
            Peak = peak;
        }
    }
}
