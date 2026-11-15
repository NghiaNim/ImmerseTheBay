using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using ImmerseTheBay.Audio;
using ImmerseTheBay.Tracking;
using Unity.Collections;
using Unity.XR.XREAL;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ImmerseTheBay.Learning
{
    [Serializable]
    public class LearningPlaneSnapshot
    {
        public bool horizontalAtDeskHeight;
    }

    [Serializable]
    public class LearningContextSnapshot
    {
        public string gesture;
        public string pointedAt;
        public string[] roomLabels = Array.Empty<string>();
        public LearningPlaneSnapshot planes = new();
        public string gaze = "room";
        public float rms;
        public string glassesKey;
    }

    /// <summary>Observes existing tracking modules; only deliberate gestures emit lesson intents.</summary>
    public class LearningContext : MonoBehaviour
    {
        public Camera headCamera;
        public Renderer viskoScreen;
        public RectTransform overlayPanel;
        public MicrophoneCapture microphone;
        public ARMeshManager meshManager;
        public ARPlaneManager planeManager;
        public bool enableScreenPinchShortcuts = true;
        [Min(0.6f)] public float longPinchSeconds = 0.6f;
        [Min(0.2f)] public float classificationInterval = 2f;
        [Min(0.1f)] public float pointingDistance = 3f;
        [Min(0.001f)] public float voiceThreshold = 0.015f;
        [Min(0.05f)] public float voiceOnsetSeconds = 0.12f;
        [Min(0.1f)] public float voiceSilenceSeconds = 0.7f;

        public event Action<LearningContextSnapshot> Intent;
        public bool VoiceActive { get; private set; }
        public string Status { get; private set; } = "observing";

        struct PinchState { public bool active, longSent; public float began; }
        PinchState m_Left, m_Right;
        readonly ConcurrentQueue<Action> m_MainThread = new();
        readonly HashSet<string> m_Labels = new();
        string[] m_RoomLabels = Array.Empty<string>();
        float m_NextScan, m_LastVoice, m_LastPoint = -10f;
        float m_VoiceBegan = -1f;
        bool m_ClassificationFailed;
        Func<string> m_Debug;

        void Awake()
        {
            if (!headCamera) headCamera = Camera.main;
            if (!microphone) microphone = FindAnyObjectByType<MicrophoneCapture>();
            if (!meshManager) meshManager = FindAnyObjectByType<ARMeshManager>();
            if (!planeManager) planeManager = FindAnyObjectByType<ARPlaneManager>();
            m_Debug = () => $"<b>== LEARNING CONTEXT ==</b>\n{Status}\ngaze {Gaze()}  voice {VoiceActive}  RMS {Rms():F3}\nroom {string.Join(", ", m_RoomLabels)}";
        }

        void OnEnable()
        {
            DebugRegistry.Register(m_Debug);
            XREALCallbackHandler.OnXREALGlassesKeyClick += OnGlassesKey;
            m_NextScan = 0;
            m_ClassificationFailed = false;
        }

        void OnDisable()
        {
            XREALCallbackHandler.OnXREALGlassesKeyClick -= OnGlassesKey;
            DebugRegistry.Unregister(m_Debug);
            m_Left = m_Right = default;
            VoiceActive = false;
            m_VoiceBegan = -1f;
            while (m_MainThread.TryDequeue(out _)) { }
        }

        void Update()
        {
            while (m_MainThread.TryDequeue(out var action)) action();
            if (!headCamera) headCamera = Camera.main;
            if (Time.unscaledTime >= m_NextScan)
            {
                m_NextScan = Time.unscaledTime + Mathf.Max(0.2f, classificationInterval);
                ScanRoom();
            }
            UpdateVoice();
            var hands = HandInput.Instance;
            // Overlay buttons own their UI pinches; selecting Hint/Speak must not also emit Next.
            bool focused = enableScreenPinchShortcuts && Gaze() == "visko";
            var left = ReadPinch(hands ? hands.Left : default, ref m_Left, focused);
            var right = ReadPinch(hands ? hands.Right : default, ref m_Right, focused);
            // Coalesce simultaneous hands: a hint must never also advance the line.
            var gesture = left == "long_pinch" || right == "long_pinch" ? "long_pinch" : left ?? right;
            if (gesture != null) Emit(Capture(gesture));
        }

        string ReadPinch(HandState hand, ref PinchState state, bool focused)
        {
            // HandInput also emits pinchUp on tracking loss; that is not a deliberate release.
            if (!hand.tracked || !focused) { state = default; return null; }
            if (hand.pinchDown) state = new PinchState { active = true, began = Time.unscaledTime };
            if (!state.active) return null;
            if (hand.pinching && !state.longSent && Time.unscaledTime - state.began >= Mathf.Max(0.6f, longPinchSeconds))
            {
                state.longSent = true;
                return "long_pinch";
            }
            if (!hand.pinching)
            {
                string release = null;
                if (hand.pinchUp && !state.longSent)
                    release = Time.unscaledTime - state.began >= Mathf.Max(0.6f, longPinchSeconds) ? "long_pinch" : "pinch";
                state = default;
                return release;
            }
            return null;
        }

        public LearningContextSnapshot Capture(string gesture = null, string pointedAt = null, string glassesKey = null) =>
            new()
            {
                gesture = gesture, pointedAt = pointedAt, glassesKey = glassesKey,
                roomLabels = (string[])m_RoomLabels.Clone(), gaze = Gaze(), rms = Rms(),
                planes = new LearningPlaneSnapshot { horizontalAtDeskHeight = HasDeskPlane() }
            };

        void Emit(LearningContextSnapshot snapshot)
        {
            Status = snapshot.gesture ?? snapshot.glassesKey ?? "observing";
            Intent?.Invoke(snapshot);
        }

        void OnGlassesKey(XREALClickType click, XREALKeyType key)
        {
            if (key != XREALKeyType.MULTI_KEY) return;
            string action = click == XREALClickType.CLICK ? "next" : click == XREALClickType.LONG_PRESS ? "repeat" : null;
            // Double-click remains owned by the recenter module.
            if (action != null) m_MainThread.Enqueue(() => { if (isActiveAndEnabled) Emit(Capture(glassesKey: action)); });
        }

        float Rms()
        {
            float value = microphone && microphone.IsRecording ? microphone.Rms : 0;
            return float.IsNaN(value) || float.IsInfinity(value) ? 0 : Mathf.Clamp01(value);
        }

        void UpdateVoice()
        {
            if (!microphone || !microphone.IsRecording) { VoiceActive = false; m_VoiceBegan = -1f; return; }
            if (Rms() >= Mathf.Max(0.001f, voiceThreshold) * (VoiceActive ? 0.65f : 1f))
            {
                if (m_VoiceBegan < 0) m_VoiceBegan = Time.unscaledTime;
                if (Time.unscaledTime - m_VoiceBegan >= Mathf.Max(0.05f, voiceOnsetSeconds)) VoiceActive = true;
                m_LastVoice = Time.unscaledTime;
            }
            else
            {
                m_VoiceBegan = -1f;
                if (Time.unscaledTime - m_LastVoice >= Mathf.Max(0.1f, voiceSilenceSeconds)) VoiceActive = false;
            }
        }

        bool HasDeskPlane()
        {
            if (!headCamera || !planeManager) return false;
            foreach (var plane in planeManager.trackables)
            {
                if (!Usable(plane) || plane.alignment != PlaneAlignment.HorizontalUp) continue;
                float belowHead = headCamera.transform.position.y - plane.center.y;
                if (belowHead >= 0.6f && belowHead <= 0.9f) return true;
            }
            return false;
        }

        static bool Usable(ARPlane plane) => plane && !plane.subsumedBy && plane.trackingState == TrackingState.Tracking;

        string Gaze()
        {
            if (!headCamera) return "room";
            var ray = new Ray(headCamera.transform.position, headCamera.transform.forward);
            float overlayDistance = float.PositiveInfinity, screenDistance = float.PositiveInfinity;
            if (overlayPanel && overlayPanel.gameObject.activeInHierarchy &&
                HitSurface(ray, overlayPanel, Vector3.zero, out var overlayPoint, out var od) &&
                overlayPanel.rect.Contains(new Vector2(overlayPoint.x, overlayPoint.y))) overlayDistance = od;
            if (viskoScreen && viskoScreen.enabled && viskoScreen.gameObject.activeInHierarchy)
            {
                var filter = viskoScreen.GetComponent<MeshFilter>();
                if (filter && filter.sharedMesh)
                {
                    var bounds = filter.sharedMesh.bounds;
                    if (HitSurface(ray, viskoScreen.transform, bounds.center, out var point, out var sd) &&
                        point.x >= bounds.min.x && point.x <= bounds.max.x && point.y >= bounds.min.y && point.y <= bounds.max.y)
                        screenDistance = sd;
                }
            }
            return overlayDistance < screenDistance ? "overlay" : float.IsPositiveInfinity(screenDistance) ? "room" : "visko";
        }

        static bool HitSurface(Ray ray, Transform surface, Vector3 center, out Vector3 localPoint, out float distance)
        {
            localPoint = default;
            var plane = new Plane(surface.forward, surface.TransformPoint(center));
            if (!plane.Raycast(ray, out distance) || distance < 0) return false;
            localPoint = surface.InverseTransformPoint(ray.GetPoint(distance));
            return true;
        }

        bool CanClassify() => !m_ClassificationFailed && meshManager && meshManager.subsystem != null &&
            meshManager.subsystem.running && XREALUtility.IsLoaderActive();

        void ScanRoom()
        {
            m_Labels.Clear();
            if (CanClassify())
            {
                try
                {
                    foreach (var filter in meshManager.meshes)
                    {
                        if (!filter || !TryMeshId(filter.name, out var id)) continue;
                        using var labels = meshManager.subsystem.GetFaceClassifications(id, Allocator.Temp);
                        foreach (var value in labels) { var label = Label(value); if (label != null) m_Labels.Add(label); }
                    }
                }
                catch (Exception) { m_ClassificationFailed = true; Status = "mesh classification unavailable"; }
            }
            if (planeManager)
                foreach (var plane in planeManager.trackables)
                    if (Usable(plane)) { var label = PlaneLabel(plane); if (label != null) m_Labels.Add(label); }
            m_RoomLabels = new string[m_Labels.Count];
            m_Labels.CopyTo(m_RoomLabels);
            Array.Sort(m_RoomLabels, StringComparer.Ordinal);
        }

        /// <summary>Explicit menu action. HandInput lacks index orientation, so palm-forward is only an approximate pointing ray.</summary>
        public void Point()
        {
            if (!isActiveAndEnabled || Time.unscaledTime - m_LastPoint < 0.4f) return;
            m_LastPoint = Time.unscaledTime;
            var hands = HandInput.Instance;
            if (!hands) { Status = "point: hands unavailable"; return; }
            var hand = hands.Right.tracked ? hands.Right : hands.Left;
            if (!hand.tracked) { Status = "point: hand not tracked"; return; }
            var ray = new Ray(hand.indexTip, hand.palmRotation * Vector3.forward);
            float nearest = Mathf.Max(0.1f, pointingDistance);
            string target = null;
            if (meshManager)
            {
                foreach (var filter in meshManager.meshes)
                {
                    if (!filter || !filter.sharedMesh) continue;
                    var collider = filter.GetComponent<MeshCollider>();
                    if (!collider || !collider.enabled || collider.sharedMesh != filter.sharedMesh ||
                        !collider.Raycast(ray, out var hit, nearest)) continue;
                    // Unknown surfaces still occlude labels behind them, even when classification is unavailable.
                    nearest = hit.distance;
                    target = null;
                    if (!CanClassify() || !TryMeshId(filter.name, out var id)) continue;
                    try
                    {
                        using var labels = meshManager.subsystem.GetFaceClassifications(id, Allocator.Temp);
                        target = HitLabel(filter.sharedMesh, hit.triangleIndex, labels);
                    }
                    catch (Exception) { m_ClassificationFailed = true; Status = "point classification unavailable"; }
                }
            }
            if (planeManager)
                foreach (var plane in planeManager.trackables)
                {
                    if (!Usable(plane) || !HitPlane(ray, plane, out var distance) || distance >= nearest) continue;
                    nearest = distance;
                    target = PlaneLabel(plane);
                }
            if (target != null) Emit(Capture("point", target));
            else Status = "point: no classified surface (mesh colliders required)";
        }

        static bool HitPlane(Ray ray, ARPlane plane, out float distance)
        {
            distance = 0;
            if (!new Plane(plane.transform.up, plane.center).Raycast(ray, out distance) || distance < 0) return false;
            var local = plane.transform.InverseTransformPoint(ray.GetPoint(distance));
            var point = new Vector2(local.x, local.z);
            var boundary = plane.boundary;
            if (!boundary.IsCreated || boundary.Length < 3)
            {
                var delta = point - plane.centerInPlaneSpace;
                return Mathf.Abs(delta.x) <= plane.extents.x && Mathf.Abs(delta.y) <= plane.extents.y;
            }
            bool inside = false;
            for (int i = 0, j = boundary.Length - 1; i < boundary.Length; j = i++)
            {
                var a = boundary[i]; var b = boundary[j];
                if ((a.y > point.y) != (b.y > point.y) && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        static string HitLabel(Mesh mesh, int triangle, NativeArray<NRMeshingVertexSemanticLabel> labels)
        {
            if (triangle < 0 || !labels.IsCreated || labels.Length == 0) return null;
            var indices = mesh.triangles;
            if (triangle >= indices.Length / 3) return null;
            // SDK documentation says face labels, but its sample uses vertex indices. Never guess an ambiguous layout.
            bool perFace = labels.Length == indices.Length / 3;
            bool perVertex = labels.Length == mesh.vertexCount;
            if (perFace == perVertex) return null;
            if (perFace) return Label(labels[triangle]);
            int offset = triangle * 3;
            var a = labels[indices[offset]]; var b = labels[indices[offset + 1]]; var c = labels[indices[offset + 2]];
            return a == b || a == c ? Label(a) : b == c ? Label(b) : null;
        }

        static string Label(NRMeshingVertexSemanticLabel label) => label switch
        {
            NRMeshingVertexSemanticLabel.Wall => "wall", NRMeshingVertexSemanticLabel.Building => "building",
            NRMeshingVertexSemanticLabel.Floor => "floor", NRMeshingVertexSemanticLabel.Ceiling => "ceiling",
            NRMeshingVertexSemanticLabel.Highway => "highway", NRMeshingVertexSemanticLabel.Sidewalk => "sidewalk",
            NRMeshingVertexSemanticLabel.Grass => "grass", NRMeshingVertexSemanticLabel.Door => "door",
            NRMeshingVertexSemanticLabel.Table => "table", _ => null
        };

        static string PlaneLabel(ARPlane plane)
        {
            var labels = plane.classifications;
            if ((labels & PlaneClassifications.Table) != 0) return "table";
            if ((labels & PlaneClassifications.DoorFrame) != 0) return "door";
            if ((labels & PlaneClassifications.Floor) != 0) return "floor";
            if ((labels & PlaneClassifications.Ceiling) != 0) return "ceiling";
            if ((labels & PlaneClassifications.WallFace) != 0) return "wall";
            return null;
        }

        static bool TryMeshId(string name, out TrackableId id)
        {
            id = TrackableId.invalidId;
            int space = name.LastIndexOf(' ');
            if (space < 0) return false;
            var parts = name.Substring(space + 1).Split('-');
            if (parts.Length != 2 || !ulong.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ||
                !ulong.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return false;
            id = new TrackableId(a, b);
            return true;
        }
    }
}
