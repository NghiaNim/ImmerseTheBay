using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ImmerseTheBay.Audio;
using ImmerseTheBay.Learning;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace ImmerseTheBay.Visko
{
    /// <summary>Japanese utterance recognition. Temporarily suspends Unity PCM capture while Android owns the mic.</summary>
    public sealed class AndroidSpeechToText : MonoBehaviour, ISpeechToText
    {
        public event Action<string> PartialResult;
        public event Action<string> FinalResult;
        public event Action<string> Error;
        // Includes permission, startup, and final-result wait; callers must not start a second utterance.
        public bool IsListening { get; private set; }
        public string Status { get; private set; } = "idle";

        static AndroidSpeechToText s_MicrophoneOwner;
        readonly List<CaptureState> m_Captures = new();
        int m_Request;
        bool m_Disposed, m_Cancelled, m_ReleasePending, m_NativeStarted, m_StopRequested;
        float m_PermissionDeadline;
        string m_CancellationError;
        Func<string> m_Debug;
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject m_Bridge;
        PermissionCallbacks m_PermissionCallbacks;
#endif

        struct CaptureState
        {
            public MicrophoneCapture capture;
            public bool wasRecording;
        }

        void Awake() => m_Debug = () => $"<b>== JAPANESE SPEECH ==</b>\n{Status}";
        void OnEnable()
        {
            SpeechCallbackPump.Ensure();
            m_Disposed = false;
            DebugRegistry.Register(m_Debug);
        }
        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            Dispose();
        }
        void OnApplicationPause(bool paused)
        {
            if (paused) Dispose();
            else if (isActiveAndEnabled) m_Disposed = false;
        }
        void Update()
        {
            if (m_PermissionDeadline > 0 && Time.realtimeSinceStartup >= m_PermissionDeadline)
                Finish(m_Request, "", "Microphone permission request timed out");
        }

        public void StartListening()
        {
            if (m_Disposed || !isActiveAndEnabled) return;
            if (IsListening || m_ReleasePending) return;
            if (!ReferenceEquals(s_MicrophoneOwner, null))
            {
                Error?.Invoke("Another speech recognizer is using the microphone");
                return;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            s_MicrophoneOwner = this;
            IsListening = true;
            m_Cancelled = false;
            m_CancellationError = null;
            m_NativeStarted = m_StopRequested = false;
            Status = "preparing microphone";
            int request = ++m_Request;
            // Never stop PCM capture reentrantly from its OnPcmChunk event.
            SpeechCallbackPump.Post(() =>
            {
                try { Begin(request); }
                catch (Exception error) { Finish(request, "", "Could not acquire microphone: " + error.Message); }
            });
#else
            Error?.Invoke("Android speech recognition requires an Android device");
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void Begin(int request)
        {
            if (!IsCurrent(request)) return;
            foreach (var capture in FindObjectsByType<MicrophoneCapture>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!capture.enabled) continue;
                m_Captures.Add(new CaptureState { capture = capture, wasRecording = capture.IsRecording });
                // OnDisable cancels pending permission/start coroutines too, not just active recordings.
                capture.enabled = false;
                capture.StopCapture();
            }
            if (Permission.HasUserAuthorizedPermission(Permission.Microphone)) StartNative(request);
            else
            {
                Status = "waiting for microphone permission";
                m_PermissionDeadline = Time.realtimeSinceStartup + 30;
                m_PermissionCallbacks = new PermissionCallbacks();
                m_PermissionCallbacks.PermissionGranted += _ => SpeechCallbackPump.Post(() => StartNative(request));
                m_PermissionCallbacks.PermissionDenied += _ => SpeechCallbackPump.Post(() => Finish(request, "", "Microphone permission was denied"));
                m_PermissionCallbacks.PermissionRequestDismissed += _ => SpeechCallbackPump.Post(() => Finish(request, "", "Microphone permission request was dismissed"));
                Permission.RequestUserPermission(Permission.Microphone, m_PermissionCallbacks);
            }
        }

        void StartNative(int request)
        {
            if (!IsCurrent(request)) return;
            m_PermissionDeadline = 0;
            m_PermissionCallbacks = null;
            try
            {
                if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
                {
                    Finish(request, "", "Microphone permission is required");
                    return;
                }
                if (m_Bridge == null)
                {
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    m_Bridge = new AndroidJavaObject("com.immersethebay.visko.JapaneseSpeechRecognizer", activity, new Listener(this));
                }
                Status = "listening (ja-JP)";
                m_NativeStarted = true;
                if (!m_Bridge.Call<bool>("startListening", request))
                    Finish(request, "", "Android could not schedule speech recognition");
            }
            catch (Exception error)
            {
                string message = "Could not start speech recognition: " + error.Message;
                if (m_Bridge == null) Finish(request, "", message);
                else
                {
                    // A failed JNI return can still follow a queued native start. Acknowledge cleanup first.
                    Dispose();
                    if (isActiveAndEnabled) m_Disposed = false;
                    Error?.Invoke(message);
                }
            }
        }

        sealed class Listener : AndroidJavaProxy
        {
            readonly AndroidSpeechToText m_Owner;
            public Listener(AndroidSpeechToText owner) : base("com.immersethebay.visko.JapaneseSpeechRecognizer$Listener") => m_Owner = owner;
            void onPartial(int request, string text) => SpeechCallbackPump.Post(() =>
            {
                if (m_Owner.IsCurrent(request)) m_Owner.PartialResult?.Invoke(text);
            });
            void onFinished(int request, string text, string error) => SpeechCallbackPump.Post(() => m_Owner.Finish(request, text, error));
        }
#endif

        bool IsCurrent(int request) => request == m_Request && IsListening && !m_Cancelled && !m_Disposed;

        public void StopListening()
        {
            if (!IsListening || m_Cancelled || m_StopRequested) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!m_NativeStarted)
            {
                m_CancellationError = "Speech recognition was cancelled before listening";
                Dispose();
                if (isActiveAndEnabled) m_Disposed = false;
                return;
            }
            m_StopRequested = true;
            Status = "recognizing Japanese";
            try
            {
                if (!m_Bridge.Call<bool>("stopListening", m_Request))
                {
                    m_CancellationError = "Android could not schedule stopping speech recognition";
                    Dispose();
                }
            }
            catch (Exception error) { Dispose(); Error?.Invoke("Could not stop speech recognition: " + error.Message); }
#endif
        }

        public void Dispose()
        {
            m_Disposed = true;
            m_Cancelled = true;
            m_PermissionDeadline = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
            m_PermissionCallbacks = null;
            if (m_Bridge != null)
            {
                if (m_ReleasePending) return;
                m_ReleasePending = true;
                Status = "releasing microphone";
                // Restore PCM only once Android acknowledges cancel/destroy in onFinished.
                try
                {
                    if (!m_Bridge.Call<bool>("dispose", m_Request))
                        Debug.LogError("[Speech] Android could not schedule microphone cleanup; PCM remains suspended");
                }
                catch (Exception error) { Debug.LogError("[Speech] Native microphone cleanup could not be requested: " + error.Message); }
                return;
            }
#endif
            Finish(m_Request, "", "");
        }

        void Finish(int request, string text, string error)
        {
            if (request != m_Request) return;
            if (!IsListening && !m_ReleasePending) return;
            bool cancelled = m_Cancelled;
            string cancellationError = m_CancellationError;
            m_CancellationError = null;
            IsListening = false;
            m_NativeStarted = false;
            m_PermissionDeadline = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
            m_PermissionCallbacks = null;
            if (m_ReleasePending)
            {
                m_Bridge?.Dispose();
                m_Bridge = null;
            }
#endif
            m_ReleasePending = false;
            foreach (var state in m_Captures)
            {
                if (!state.capture) continue;
                bool autoStart = state.capture.startOnEnable;
                state.capture.startOnEnable = false;
                state.capture.enabled = true;
                state.capture.startOnEnable = autoStart;
                if (state.wasRecording) state.capture.StartCapture();
            }
            m_Captures.Clear();
            if (ReferenceEquals(s_MicrophoneOwner, this)) s_MicrophoneOwner = null;
            Status = cancelled ? "stopped" : string.IsNullOrEmpty(error) ? "ready" : error;
            if (cancelled)
            {
                if (!string.IsNullOrEmpty(cancellationError) && this && isActiveAndEnabled)
                    Error?.Invoke(cancellationError);
                return;
            }
            if (!string.IsNullOrEmpty(error)) Error?.Invoke(error);
            else if (!string.IsNullOrEmpty(text)) FinalResult?.Invoke(text);
        }
    }

    // A persistent Update queue also receives cleanup acknowledgements after the source object is disabled/destroyed.
    sealed class SpeechCallbackPump : MonoBehaviour
    {
        static readonly ConcurrentQueue<Action> s_Queue = new();
        static SpeechCallbackPump s_Instance;
        internal static void Ensure()
        {
            if (s_Instance) return;
            var owner = new GameObject("Speech callbacks") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(owner);
            s_Instance = owner.AddComponent<SpeechCallbackPump>();
        }
        internal static void Post(Action action) => s_Queue.Enqueue(action);
        void Update()
        {
            int count = s_Queue.Count;
            for (int i = 0; i < count && s_Queue.TryDequeue(out var action); i++)
            {
                try { action(); }
                catch (Exception error) { Debug.LogException(error); }
            }
        }
    }
}
