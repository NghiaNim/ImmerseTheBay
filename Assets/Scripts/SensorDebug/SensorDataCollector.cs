using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Hands;
using XRInputDevice = UnityEngine.XR.InputDevice;
using XRCommonUsages = UnityEngine.XR.CommonUsages;

namespace ImmerseTheBay.SensorDebug
{
    /// <summary>
    /// Collects every piece of data exposed by the XREAL XR plugin through the standard Unity XR stack
    /// (XR Hands, AR Foundation, XR InputDevices) plus the host phone / Beam Pro sensors (Input System),
    /// and formats it into text sections for <see cref="SensorDataHud"/>.
    /// </summary>
    public class SensorDataCollector : MonoBehaviour
    {
        [Header("Optional AR managers (auto-found if empty)")]
        public ARSession arSession;
        public ARPlaneManager planeManager;
        public ARMeshManager meshManager;
        public ARAnchorManager anchorManager;
        public ARTrackedImageManager imageManager;

        [Header("Hand joint visualisation")]
        public bool drawHandJoints = true;
        public float jointSize = 0.012f;

        public string SectionHead { get; private set; } = "";
        public string SectionHands { get; private set; } = "";
        public string SectionAR { get; private set; } = "";
        public string SectionDevices { get; private set; } = "";
        public string SectionSensors { get; private set; } = "";
        public string SectionSystem { get; private set; } = "";
        public string SectionExtras { get; private set; } = "";
        public string SectionXreal { get; private set; } = "";
        public float FramesPerSecond => m_Fps;

        static readonly List<XRHandSubsystem> s_HandSubsystems = new();
        static readonly List<XRInputSubsystem> s_InputSubsystems = new();
        static readonly List<XRDisplaySubsystem> s_DisplaySubsystems = new();
        static readonly List<XRMeshSubsystem> s_MeshSubsystems = new();
        static readonly List<XRInputDevice> s_Devices = new();
        static readonly List<InputFeatureUsage> s_Usages = new();

        static readonly XRHandJointID[] s_KeyJoints =
        {
            XRHandJointID.Wrist, XRHandJointID.Palm, XRHandJointID.ThumbTip,
            XRHandJointID.IndexTip, XRHandJointID.MiddleTip, XRHandJointID.RingTip, XRHandJointID.LittleTip
        };

        readonly Dictionary<int, Transform> m_JointVisuals = new();
        XRHandSubsystem m_Hands;
        float m_FpsAccum; int m_FpsFrames; float m_Fps;
        int m_MeshesAdded, m_MeshesUpdated, m_MeshesRemoved;
        readonly StringBuilder m_Sb = new(4096);

        void Awake()
        {
            // Existing generated scenes gain logging without changing the integration-owned builder or scene.
            if (!GetComponent<SensorLogger>()) gameObject.AddComponent<SensorLogger>();
        }

        void OnEnable()
        {
            if (!arSession) arSession = FindAnyObjectByType<ARSession>();
            if (!planeManager) planeManager = FindAnyObjectByType<ARPlaneManager>();
            if (!meshManager) meshManager = FindAnyObjectByType<ARMeshManager>();
            if (!anchorManager) anchorManager = FindAnyObjectByType<ARAnchorManager>();
            if (!imageManager) imageManager = FindAnyObjectByType<ARTrackedImageManager>();
            if (meshManager) meshManager.meshesChanged += OnMeshesChanged;
            EnableAllSensors();
            InputSystem.onDeviceChange += OnDeviceChange;
        }

        void OnDisable()
        {
            if (meshManager) meshManager.meshesChanged -= OnMeshesChanged;
            InputSystem.onDeviceChange -= OnDeviceChange;
        }

        void OnMeshesChanged(ARMeshesChangedEventArgs e)
        {
            m_MeshesAdded += e.added.Count; m_MeshesUpdated += e.updated.Count; m_MeshesRemoved += e.removed.Count;
        }

        void OnDeviceChange(UnityEngine.InputSystem.InputDevice d, InputDeviceChange c)
        {
            if (c == InputDeviceChange.Added && d is Sensor) InputSystem.EnableDevice(d);
        }

        static void EnableAllSensors()
        {
            foreach (var d in InputSystem.devices)
                if (d is Sensor && !d.enabled) InputSystem.EnableDevice(d);
        }

        void Update()
        {
            m_FpsAccum += Time.unscaledDeltaTime; m_FpsFrames++;
            if (m_FpsAccum >= 0.5f) { m_Fps = m_FpsFrames / m_FpsAccum; m_FpsAccum = 0; m_FpsFrames = 0; }

            RefreshHands();
            if (drawHandJoints) UpdateJointVisuals();
        }

        void RefreshHands()
        {
            if (m_Hands != null && m_Hands.running) return;
            SubsystemManager.GetSubsystems(s_HandSubsystems);
            m_Hands = s_HandSubsystems.Find(s => s.running);
        }

        /// <summary>Numeric data for recording, captured on the main thread without rebuilding HUD text.</summary>
        public struct Sample
        {
            public Vector3? headPosition, headVelocity;
            public Quaternion? headRotation;
            public HandSample left, right;
            public string sessionState;
            public int planeCount, meshChunks, meshVertices, anchorCount;
        }

        public struct HandSample
        {
            public bool tracked;
            public Vector3? wrist, indexTip, thumbTip;
            public float? pinchCm;
        }

        public Sample CaptureSample()
        {
            var sample = new Sample
            {
                sessionState = ARSession.state.ToString(),
                planeCount = planeManager ? planeManager.trackables.count : 0,
                anchorCount = anchorManager ? anchorManager.trackables.count : 0,
            };
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (head.isValid)
            {
                if (head.TryGetFeatureValue(XRCommonUsages.devicePosition, out Vector3 p)) sample.headPosition = p;
                if (head.TryGetFeatureValue(XRCommonUsages.deviceRotation, out Quaternion q)) sample.headRotation = q;
                if (head.TryGetFeatureValue(XRCommonUsages.deviceVelocity, out Vector3 v)) sample.headVelocity = v;
            }
            else if (Camera.main)
            {
                // Editor fallback: local camera pose; unavailable XR velocity remains empty in the CSV.
                sample.headPosition = Camera.main.transform.localPosition;
                sample.headRotation = Camera.main.transform.localRotation;
            }

            RefreshHands();
            if (m_Hands != null)
            {
                sample.left = CaptureHand(m_Hands.leftHand);
                sample.right = CaptureHand(m_Hands.rightHand);
            }
            if (meshManager)
            {
                sample.meshChunks = meshManager.meshes.Count;
                foreach (var mesh in meshManager.meshes)
                    if (mesh && mesh.sharedMesh) sample.meshVertices += mesh.sharedMesh.vertexCount;
            }
            return sample;
        }

        HandSample CaptureHand(XRHand hand)
        {
            var sample = new HandSample { tracked = hand.isTracked };
            if (!hand.isTracked) return sample;
            // Reuse the collector's existing XR Hands source. HandInput defines its world-space origin.
            var hands = Tracking.HandInput.Instance;
            var space = hands ? hands.transform : transform;
            if (hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out var w)) sample.wrist = space.TransformPoint(w.position);
            if (hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var i)) sample.indexTip = space.TransformPoint(i.position);
            if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var t)) sample.thumbTip = space.TransformPoint(t.position);
            if (sample.indexTip.HasValue && sample.thumbTip.HasValue)
                sample.pinchCm = Vector3.Distance(sample.indexTip.Value, sample.thumbTip.Value) * 100f;
            return sample;
        }

        /// <summary>Rebuilds all text sections. Called by the HUD at its refresh rate.</summary>
        public void Collect()
        {
            SectionHead = BuildHead();
            SectionHands = BuildHands();
            SectionAR = BuildAR();
            SectionDevices = BuildDevices();
            SectionSensors = BuildSensors();
            SectionSystem = BuildSystem();
            SectionExtras = DebugRegistry.Build();
            SectionXreal = DebugRegistry.Build("ImmerseTheBay.Xreal");
        }

        // ---------------------------------------------------------------- Head / tracking
        string BuildHead()
        {
            var sb = m_Sb.Clear();
            sb.AppendLine("<b>== HEAD / TRACKING ==</b>");
            var head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (head.isValid)
            {
                head.TryGetFeatureValue(XRCommonUsages.trackingState, out InputTrackingState ts);
                head.TryGetFeatureValue(XRCommonUsages.devicePosition, out Vector3 p);
                head.TryGetFeatureValue(XRCommonUsages.deviceRotation, out Quaternion r);
                head.TryGetFeatureValue(XRCommonUsages.deviceVelocity, out Vector3 v);
                head.TryGetFeatureValue(XRCommonUsages.deviceAngularVelocity, out Vector3 av);
                sb.AppendLine($"Device: {head.name}");
                sb.AppendLine($"TrackingState: {ts}");
                sb.AppendLine($"Pos: {F(p)}");
                sb.AppendLine($"Rot(euler): {F(r.eulerAngles)}");
                sb.AppendLine($"Vel: {F(v)}  AngVel: {F(av)}");
            }
            else sb.AppendLine("Head device: <color=#f88>not found</color>");

            var cam = Camera.main;
            if (cam) sb.AppendLine($"Camera world pos: {F(cam.transform.position)}  fwd: {F(cam.transform.forward)}");

            SubsystemManager.GetSubsystems(s_InputSubsystems);
            foreach (var s in s_InputSubsystems)
                sb.AppendLine($"InputSubsystem[{s.subsystemDescriptor.id}] running={s.running} origin={s.GetTrackingOriginMode()}");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- Hands
        string BuildHands()
        {
            var sb = m_Sb.Clear();
            sb.AppendLine("<b>== HAND TRACKING (XR Hands) ==</b>");
            if (m_Hands == null) { sb.AppendLine("<color=#f88>No running XRHandSubsystem</color> (set XREAL Input Source = Hands)"); return sb.ToString(); }
            sb.AppendLine($"Subsystem: {m_Hands.subsystemDescriptor.id}");
            AppendHand(sb, m_Hands.leftHand, "LEFT");
            AppendHand(sb, m_Hands.rightHand, "RIGHT");
            return sb.ToString();
        }

        static void AppendHand(StringBuilder sb, XRHand hand, string label)
        {
            if (!hand.isTracked) { sb.AppendLine($"{label}: <color=#aaa>not tracked</color>"); return; }
            sb.AppendLine($"{label}: tracked  root {F(hand.rootPose.position)}");
            foreach (var id in s_KeyJoints)
                if (hand.GetJoint(id).TryGetPose(out var pose))
                    sb.AppendLine($"  {id,-10} {F(pose.position)}");
            if (hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var t) &&
                hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var i))
                sb.AppendLine($"  Pinch dist: {Vector3.Distance(t.position, i.position) * 100f:F1} cm");
            int valid = 0;
            for (var j = XRHandJointID.BeginMarker; j < XRHandJointID.EndMarker; j++)
                if (hand.GetJoint(j).TryGetPose(out _)) valid++;
            sb.AppendLine($"  Valid joints: {valid}/{XRHandJointID.EndMarker.ToIndex()}");
        }

        void UpdateJointVisuals()
        {
            int k = 0;
            if (m_Hands != null)
            {
                foreach (var hand in new[] { m_Hands.leftHand, m_Hands.rightHand })
                {
                    if (!hand.isTracked) continue;
                    for (var j = XRHandJointID.BeginMarker; j < XRHandJointID.EndMarker; j++)
                    {
                        if (!hand.GetJoint(j).TryGetPose(out var pose)) continue;
                        var v = GetJointVisual(k++);
                        v.gameObject.SetActive(true);
                        v.localPosition = pose.position;
                        v.localRotation = pose.rotation;
                    }
                }
            }
            foreach (var kv in m_JointVisuals) if (kv.Key >= k) kv.Value.gameObject.SetActive(false);
        }

        Transform GetJointVisual(int idx)
        {
            if (m_JointVisuals.TryGetValue(idx, out var t)) return t;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(go.GetComponent<Collider>());
            go.name = $"Joint_{idx}";
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * jointSize;
            m_JointVisuals[idx] = go.transform;
            return go.transform;
        }

        // ---------------------------------------------------------------- AR Foundation
        string BuildAR()
        {
            var sb = m_Sb.Clear();
            sb.AppendLine("<b>== AR FOUNDATION ==</b>");
            sb.AppendLine($"ARSession.state: {ARSession.state}  notTrackingReason: {ARSession.notTrackingReason}");
            if (arSession && arSession.subsystem != null)
                sb.AppendLine($"Session trackingState: {arSession.subsystem.trackingState}  mode: {arSession.subsystem.requestedTrackingMode}");

            // Meshing
            if (meshManager)
            {
                int verts = 0, tris = 0;
                var meshes = meshManager.meshes;
                foreach (var mf in meshes)
                {
                    if (!mf || !mf.sharedMesh) continue;
                    verts += mf.sharedMesh.vertexCount;
                    tris += (int)(mf.sharedMesh.GetIndexCount(0) / 3);
                }
                sb.AppendLine($"<b>Mesh</b> enabled={meshManager.enabled} chunks={meshes.Count} verts={verts} tris={tris}");
                sb.AppendLine($"  events +{m_MeshesAdded} ~{m_MeshesUpdated} -{m_MeshesRemoved}  density={meshManager.density:F2}");
            }
            else sb.AppendLine("Mesh: no ARMeshManager");
            SubsystemManager.GetSubsystems(s_MeshSubsystems);
            foreach (var m in s_MeshSubsystems) sb.AppendLine($"  XRMeshSubsystem[{m.subsystemDescriptor.id}] running={m.running}");

            // Planes
            if (planeManager)
            {
                sb.AppendLine($"<b>Planes</b> count={planeManager.trackables.count} mode={planeManager.requestedDetectionMode}");
                int n = 0;
                foreach (var plane in planeManager.trackables)
                {
                    if (n++ >= 6) { sb.AppendLine("  ..."); break; }
                    sb.AppendLine($"  {Short(plane.trackableId)} {plane.alignment} {plane.classifications} size={plane.size.x:F2}x{plane.size.y:F2} {plane.trackingState}");
                }
            }
            else sb.AppendLine("Planes: no ARPlaneManager");

            // Anchors
            if (anchorManager)
            {
                sb.AppendLine($"<b>Anchors</b> count={anchorManager.trackables.count} subsystem={anchorManager.subsystem?.GetType().Name}");
                foreach (var a in anchorManager.trackables)
                    sb.AppendLine($"  {Short(a.trackableId)} {a.trackingState} {F(a.transform.position)}");
            }

            // Images
            if (imageManager)
            {
                sb.AppendLine($"<b>Images</b> library={(imageManager.referenceLibrary != null ? imageManager.referenceLibrary.count : 0)} tracked={imageManager.trackables.count}");
                foreach (var img in imageManager.trackables)
                    sb.AppendLine($"  {img.referenceImage.name} {img.trackingState} {F(img.transform.position)}");
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- Generic XR InputDevices dump
        string BuildDevices()
        {
            var sb = m_Sb.Clear();
            sb.AppendLine("<b>== XR INPUT DEVICES (all features) ==</b>");
            InputDevices.GetDevices(s_Devices);
            if (s_Devices.Count == 0) sb.AppendLine("(none)");
            foreach (var d in s_Devices)
            {
                sb.AppendLine($"<b>{d.name}</b> [{d.characteristics}]");
                if (!d.TryGetFeatureUsages(s_Usages)) continue;
                foreach (var u in s_Usages)
                {
                    var val = ReadUsage(d, u);
                    if (val != null) sb.AppendLine($"  {u.name}: {val}");
                }
            }
            return sb.ToString();
        }

        static string ReadUsage(XRInputDevice d, InputFeatureUsage u)
        {
            var t = u.type;
            if (t == typeof(bool)) return d.TryGetFeatureValue(u.As<bool>(), out var b) ? b.ToString() : null;
            if (t == typeof(uint)) return d.TryGetFeatureValue(u.As<uint>(), out var ui) ? ui.ToString() : null;
            if (t == typeof(float)) return d.TryGetFeatureValue(u.As<float>(), out var f) ? f.ToString("F3") : null;
            if (t == typeof(Vector2)) return d.TryGetFeatureValue(u.As<Vector2>(), out var v2) ? v2.ToString("F2") : null;
            if (t == typeof(Vector3)) return d.TryGetFeatureValue(u.As<Vector3>(), out var v3) ? F(v3) : null;
            if (t == typeof(Quaternion)) return d.TryGetFeatureValue(u.As<Quaternion>(), out var q) ? F(q.eulerAngles) : null;
            if (t == typeof(InputTrackingState)) return d.TryGetFeatureValue(u.As<InputTrackingState>(), out var s) ? s.ToString() : null;
            if (t == typeof(Eyes)) return d.TryGetFeatureValue(u.As<Eyes>(), out var e) && e.TryGetFixationPoint(out var fp) ? "fixation " + F(fp) : null;
            return $"<{t.Name}>";
        }

        // ---------------------------------------------------------------- Phone / Beam Pro sensors
        string BuildSensors()
        {
            var sb = m_Sb.Clear();
            sb.AppendLine("<b>== HOST DEVICE SENSORS (Input System) ==</b>");
            bool any = false;
            foreach (var d in InputSystem.devices)
            {
                if (!(d is Sensor)) continue;
                any = true;
                sb.Append($"{d.name}{(d.enabled ? "" : " (disabled)")}: ");
                foreach (var c in d.children)
                {
                    try { sb.Append($"{c.name}={FormatValue(c.ReadValueAsObject())} "); }
                    catch (Exception) { /* some controls are not readable */ }
                }
                sb.AppendLine();
            }
            if (!any) sb.AppendLine("(no sensors reported by Input System)");
            return sb.ToString();
        }

        static string FormatValue(object o) => o switch
        {
            Vector3 v => F(v),
            Quaternion q => F(q.eulerAngles),
            float f => f.ToString("F3"),
            _ => o?.ToString() ?? "null"
        };

        // ---------------------------------------------------------------- System / display
        string BuildSystem()
        {
            var sb = m_Sb.Clear();
            sb.AppendLine("<b>== SYSTEM / DISPLAY ==</b>");
            sb.AppendLine($"FPS: {m_Fps:F1}  XR device: {XRSettings.loadedDeviceName}  XR enabled: {XRSettings.enabled}");
            sb.AppendLine($"Eye tex: {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}  renderScale: {XRSettings.eyeTextureResolutionScale:F2}  stereo: {XRSettings.stereoRenderingMode}");
            SubsystemManager.GetSubsystems(s_DisplaySubsystems);
            foreach (var ds in s_DisplaySubsystems)
                sb.AppendLine($"Display[{ds.subsystemDescriptor.id}] running={ds.running} refresh={(ds.TryGetDisplayRefreshRate(out var hz) ? hz.ToString("F0") : "?")}Hz");
            sb.AppendLine($"Model: {SystemInfo.deviceModel}  OS: {SystemInfo.operatingSystem}");
            sb.AppendLine($"GPU: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})");
            sb.AppendLine($"Battery: {SystemInfo.batteryLevel * 100f:F0}% {SystemInfo.batteryStatus}");
            sb.AppendLine($"Screen: {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio.value:F0}Hz  Time: {Time.realtimeSinceStartup:F0}s");
            return sb.ToString();
        }

        static string F(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";
        static string Short(TrackableId id) => id.ToString().Substring(0, 8);
    }
}
