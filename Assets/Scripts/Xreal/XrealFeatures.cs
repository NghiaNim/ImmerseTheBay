using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.XR.XREAL;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Keyboard = UnityEngine.InputSystem.Keyboard;

namespace ImmerseTheBay.Xreal
{
    /// <summary>
    /// XREAL-only features (Unity.XR.XREAL, SDK 3.1):
    ///  - Device info: type, category, tracking type, input source, refresh rate, hand-tracking support.
    ///  - Glasses events: wearing, temperature, brightness, volume, screen state, disconnect, power save, errors, button clicks.
    ///  - Runtime switches: input source (Hands / Controller / Both), tracking type (6DoF / 3DoF / 0DoF).
    ///  - Glasses buttons: multi-key double-click = recenter.
    ///  - Mesh classification: per-label face counts from XRMeshSubsystem.GetFaceClassifications.
    /// Everything is shown on the sensor HUD via DebugRegistry. Editor keys: I = switch input, T = cycle tracking type.
    /// </summary>
    public class XrealFeatures : MonoBehaviour
    {
        public ARMeshManager meshManager;
        public Tracking.RecenterController recenter;
        [Tooltip("Seconds between mesh classification scans (it walks every mesh chunk).")]
        public float classificationInterval = 2f;

        readonly ConcurrentQueue<string> m_Events = new();
        readonly LinkedList<string> m_RecentEvents = new();
        readonly Dictionary<NRMeshingVertexSemanticLabel, int> m_LabelCounts = new();
        readonly ConcurrentQueue<Action> m_MainThread = new();
        string m_Wearing = "?", m_Temperature = "?", m_Brightness = "?", m_Volume = "?", m_Screen = "?";
        string m_LastSwitch = "-";
        float m_NextScan;
        bool m_ClassificationSupported = true;
        Func<string> m_Debug;
        readonly StringBuilder m_Sb = new();

        void Awake()
        {
            if (!meshManager) meshManager = FindAnyObjectByType<ARMeshManager>();
            if (!recenter) recenter = FindAnyObjectByType<Tracking.RecenterController>();
            m_Debug = BuildDebug;
        }

        void OnEnable()
        {
            DebugRegistry.Register(m_Debug);
            XREALCallbackHandler.OnXREALError += OnError;
            XREALCallbackHandler.OnXREALGlassesKeyClick += OnKeyClick;
            XREALCallbackHandler.OnXREALGlassesDisconnect += OnDisconnect;
            XREALCallbackHandler.OnXREALGlassesVolume += OnVolume;
            XREALCallbackHandler.OnXREALGlassesWearingState += OnWearing;
            XREALCallbackHandler.OnXREALGlassesBrightness += OnBrightness;
            XREALCallbackHandler.OnXREALGlassesTemperatureLevel += OnTemperature;
            XREALCallbackHandler.OnXREALGlassesScreenStatus += OnScreen;
            XREALCallbackHandler.OnXREALGlassesPowerSave += OnPowerSave;
        }

        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            XREALCallbackHandler.OnXREALError -= OnError;
            XREALCallbackHandler.OnXREALGlassesKeyClick -= OnKeyClick;
            XREALCallbackHandler.OnXREALGlassesDisconnect -= OnDisconnect;
            XREALCallbackHandler.OnXREALGlassesVolume -= OnVolume;
            XREALCallbackHandler.OnXREALGlassesWearingState -= OnWearing;
            XREALCallbackHandler.OnXREALGlassesBrightness -= OnBrightness;
            XREALCallbackHandler.OnXREALGlassesTemperatureLevel -= OnTemperature;
            XREALCallbackHandler.OnXREALGlassesScreenStatus -= OnScreen;
            XREALCallbackHandler.OnXREALGlassesPowerSave -= OnPowerSave;
        }

        // Callbacks may arrive on a native thread: only touch strings / queues here.
        void OnError(XREALErrorCode code, string msg) => m_Events.Enqueue($"ERROR {code} {msg}");
        void OnDisconnect(XREALGlassesDisconnectReason r) => m_Events.Enqueue($"glasses disconnected: {r}");
        void OnVolume(uint v) => m_Volume = v.ToString();
        void OnWearing(XREALWearingStatus s) => m_Wearing = s.ToString();
        void OnBrightness(uint b) => m_Brightness = b.ToString();
        void OnTemperature(XREALTemperatureLevel l) { m_Temperature = l.ToString(); m_Events.Enqueue($"temperature {l}"); }
        void OnScreen(XREALDisplayState s) => m_Screen = s.ToString();
        void OnPowerSave() => m_Events.Enqueue("power save");

        void OnKeyClick(XREALClickType click, XREALKeyType key)
        {
            m_Events.Enqueue($"key {key} {click}");
            if (key == XREALKeyType.MULTI_KEY && click == XREALClickType.DOUBLE_CLICK)
                m_MainThread.Enqueue(() => recenter?.Recenter());
        }

        // ------------------------------------------------------------ runtime switches
        public void SwitchInputSource()
        {
            bool ok = XREALPlugin.SwitchInputSource();
            m_LastSwitch = $"input -> {XREALPlugin.GetInputSource()} ({(ok ? "ok" : "failed")})";
        }

        public void SetInputSource(InputSource source)
        {
            bool ok = XREALPlugin.SetInputSource(source);
            m_LastSwitch = $"input -> {source} ({(ok ? "ok" : "failed")})";
        }

        public async void SetTrackingType(TrackingType type)
        {
            m_LastSwitch = $"tracking -> {type}...";
            bool ok = await XREALPlugin.SwitchTrackingTypeAsync(type);
            m_LastSwitch = $"tracking -> {type} ({(ok ? "ok" : "failed")})";
        }

        public void CycleTrackingType()
        {
            var next = XREALPlugin.GetTrackingType() switch
            {
                TrackingType.MODE_6DOF => TrackingType.MODE_3DOF,
                TrackingType.MODE_3DOF => TrackingType.MODE_0DOF,
                _ => TrackingType.MODE_6DOF,
            };
            SetTrackingType(next);
        }

        // ------------------------------------------------------------ update
        void Update()
        {
            while (m_MainThread.TryDequeue(out var a)) a();
            while (m_Events.TryDequeue(out var e))
            {
                m_RecentEvents.AddFirst($"{Time.realtimeSinceStartup:F0}s {e}");
                while (m_RecentEvents.Count > 5) m_RecentEvents.RemoveLast();
            }

            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.iKey.wasPressedThisFrame) SwitchInputSource();
                if (kb.tKey.wasPressedThisFrame) CycleTrackingType();
            }

            if (m_ClassificationSupported && Time.unscaledTime >= m_NextScan)
            {
                m_NextScan = Time.unscaledTime + classificationInterval;
                ScanClassifications();
            }
        }

        void ScanClassifications()
        {
            if (!meshManager || meshManager.subsystem == null || !meshManager.subsystem.running) return;
            var mesh = meshManager.subsystem;
            m_LabelCounts.Clear();
            foreach (var mf in meshManager.meshes)
            {
                if (!mf || !TryParseMeshId(mf.name, out var id)) continue;
                try
                {
                    using var labels = mesh.GetFaceClassifications(id, Allocator.Temp);
                    foreach (var l in labels) m_LabelCounts[l] = m_LabelCounts.TryGetValue(l, out var n) ? n + 1 : 1;
                }
                catch (Exception e)
                {
                    m_ClassificationSupported = false; // e.g. classification not enabled / not supported on this device
                    m_Events.Enqueue($"mesh classification unavailable: {e.Message}");
                    return;
                }
            }
        }

        /// <summary>ARMeshManager names chunk objects "Mesh {TrackableId}".</summary>
        static bool TryParseMeshId(string name, out TrackableId id)
        {
            id = TrackableId.invalidId;
            int i = name.LastIndexOf(' ');
            if (i < 0) return false;
            var parts = name.Substring(i + 1).Split('-');
            if (parts.Length != 2) return false;
            try
            {
                id = new TrackableId(Convert.ToUInt64(parts[0], 16), Convert.ToUInt64(parts[1], 16));
                return true;
            }
            catch (FormatException) { return false; }
        }

        // ------------------------------------------------------------ HUD
        string BuildDebug()
        {
            var sb = m_Sb.Clear();
            sb.AppendLine("<b>== XREAL ==</b>");
            if (!XREALUtility.IsLoaderActive()) { sb.Append("<color=#f88>XREAL loader not active</color> (enable XREAL in XR Plug-in Management > Android)"); return sb.ToString(); }
            uint hz = 0;
            XREALPlugin.GetDeviceRefreshRate(ref hz);
            sb.AppendLine($"device {XREALPlugin.GetDeviceType()} / {XREALPlugin.GetDeviceCategory()}  {hz} Hz");
            sb.AppendLine($"tracking {XREALPlugin.GetTrackingType()}  input {XREALPlugin.GetInputSource()}  hands supported={XREALPlugin.IsHandTrackingSupported()}");
            sb.AppendLine($"wearing {m_Wearing}  temp {m_Temperature}  bright {m_Brightness}  vol {m_Volume}  screen {m_Screen}");
            sb.AppendLine($"last switch: {m_LastSwitch}   (I=input, T=tracking, double-click glasses key=recenter)");
            if (m_LabelCounts.Count > 0)
            {
                sb.Append("mesh labels:");
                foreach (var kv in m_LabelCounts) sb.Append($" {kv.Key}={kv.Value}");
                sb.AppendLine();
            }
            else if (!m_ClassificationSupported) sb.AppendLine("mesh labels: unavailable");
            foreach (var e in m_RecentEvents) sb.AppendLine($"  {e}");
            return sb.ToString();
        }
    }
}
