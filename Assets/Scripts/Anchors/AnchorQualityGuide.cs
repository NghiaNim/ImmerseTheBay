using System;
using System.Collections.Generic;
using System.Text;
using Unity.XR.XREAL;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ImmerseTheBay.Anchors
{
    /// <summary>Collects GOOD viewpoints around each anchor and guides the user before saving.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ARAnchorManager))]
    public class AnchorQualityGuide : MonoBehaviour
    {
        [Range(12, 16)] public int segmentCount = 16;
        [Tooltip("Fraction of distinct directions that must have GOOD mapping quality before saving.")]
        [Range(0f, 1f)] public float minCoverage = 0.5f;
        [Min(0.05f)] public float pollInterval = 0.2f;
        [Min(0.05f)] public float ringRadius = 0.22f;
        [Tooltip("Maximum angle between the head's forward direction and the anchor to count a viewpoint.")]
        [Range(1f, 90f)] public float viewAngle = 45f;

        sealed class Guide
        {
            public bool[] covered;
            public Renderer[] segments;
            public Transform root;
            public Text label;
            public int goodCount;
            public bool saved, sampled;
            public XREALAnchorEstimateQuality quality;
            public float Coverage => goodCount / (float)covered.Length;
        }

        readonly Dictionary<ARAnchor, Guide> m_Guides = new();
        readonly List<ARAnchor> m_Removed = new();
        readonly StringBuilder m_Text = new();
        ARAnchorManager m_Manager;
        SpatialAnchorController m_Controller;
        Material m_PendingMaterial, m_GoodMaterial;
        Font m_Font;
        float m_NextPoll;
        Func<string> m_Debug;

        void Awake()
        {
            m_Manager = GetComponent<ARAnchorManager>();
            m_Debug = BuildDebug;
        }

        void OnEnable()
        {
            m_Controller = GetComponent<SpatialAnchorController>();
            if (m_Controller)
            {
                m_Controller.AnchorPlaced += TrackPlaced;
                m_Controller.AnchorRestored += TrackRestored;
                // Also recover events missed while this component was disabled.
                foreach (var anchor in m_Controller.Unsaved) TrackPlaced(anchor);
                foreach (var pair in m_Controller.Saved) MarkSaved(pair.Key);
            }
            foreach (var guide in m_Guides.Values)
                if (guide.root) guide.root.gameObject.SetActive(!guide.saved);
            m_NextPoll = 0f;
            DebugRegistry.Register(m_Debug);
        }

        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            if (m_Controller)
            {
                m_Controller.AnchorPlaced -= TrackPlaced;
                m_Controller.AnchorRestored -= TrackRestored;
            }
            foreach (var guide in m_Guides.Values)
                if (guide.root) guide.root.gameObject.SetActive(false);
        }

        public float GetCoverage(ARAnchor anchor) =>
            anchor && m_Guides.TryGetValue(anchor, out var guide) ? guide.Coverage : 0f;

        public bool IsReadyToSave(ARAnchor anchor) =>
            isActiveAndEnabled && anchor && m_Guides.ContainsKey(anchor) && GetCoverage(anchor) >= Mathf.Clamp01(minCoverage);

        void TrackPlaced(ARAnchor anchor) => Track(anchor, false);
        void TrackRestored(ARAnchor anchor, SerializableGuid _) => MarkSaved(anchor);

        public void MarkSaved(ARAnchor anchor)
        {
            Track(anchor, true);
            if (!anchor || !m_Guides.TryGetValue(anchor, out var guide)) return;
            guide.saved = true;
            if (guide.root) guide.root.gameObject.SetActive(false);
        }

        public void ClearGuides()
        {
            foreach (var guide in m_Guides.Values)
                if (guide.root) Destroy(guide.root.gameObject);
            m_Guides.Clear();
        }

        void Track(ARAnchor anchor, bool saved)
        {
            if (!anchor || m_Guides.ContainsKey(anchor)) return;
            var guide = new Guide { covered = new bool[Mathf.Clamp(segmentCount, 12, 16)], saved = saved };
            m_Guides.Add(anchor, guide);
            if (!saved) BuildRing(anchor, guide);
        }

        void Update()
        {
            if (Time.unscaledTime < m_NextPoll) return;
            m_NextPoll = Time.unscaledTime + Mathf.Max(0.05f, pollInterval);
            var cam = Camera.main;
            bool tracking = cam && m_Manager.isActiveAndEnabled && m_Manager.subsystem != null &&
                            m_Manager.subsystem.running && ARSession.state == ARSessionState.SessionTracking;
            m_Removed.Clear();
            foreach (var pair in m_Guides)
            {
                if (!pair.Key)
                {
                    if (pair.Value.root) Destroy(pair.Value.root.gameObject);
                    m_Removed.Add(pair.Key);
                    continue;
                }
                if (tracking && pair.Key.trackingState == TrackingState.Tracking)
                    Sample(pair.Key, pair.Value, cam.transform);
                RefreshLabel(pair.Key, pair.Value);
            }
            foreach (var anchor in m_Removed) m_Guides.Remove(anchor);
        }

        void Sample(ARAnchor anchor, Guide guide, Transform head)
        {
            // XREAL 3.1 expects the world-space HMD pose, as in its Anchors sample.
            guide.quality = m_Manager.GetAnchorQuality(anchor.trackableId, new Pose(head.position, head.rotation));
            guide.sampled = true;
            if (guide.saved || guide.quality != XREALAnchorEstimateQuality.XREAL_ANCHOR_ESTIMATE_QUALITY_GOOD) return;

            var toAnchor = anchor.transform.position - head.position;
            if (toAnchor.sqrMagnitude < 0.0001f || Vector3.Angle(head.forward, toAnchor) > viewAngle) return;
            var direction = Vector3.ProjectOnPlane(-toAnchor, Vector3.up);
            // Directly above/below the anchor has no meaningful horizontal direction.
            if (direction.sqrMagnitude < 0.0025f) return;
            float angle = Mathf.Repeat(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, 360f);
            int index = Mathf.FloorToInt(Mathf.Repeat(angle + 180f / guide.covered.Length, 360f) /
                                        (360f / guide.covered.Length));
            if (guide.covered[index]) return;
            guide.covered[index] = true;
            guide.goodCount++;
            guide.segments[index].sharedMaterial = m_GoodMaterial;
        }

        void LateUpdate()
        {
            var cam = Camera.main;
            foreach (var pair in m_Guides)
            {
                var guide = pair.Value;
                if (!pair.Key || !guide.root || guide.saved) continue;
                // Keep compass directions level and fixed even if the anchor's rotation is refined.
                guide.root.SetPositionAndRotation(pair.Key.transform.position, Quaternion.identity);
                if (cam)
                {
                    var canvas = guide.label.transform.parent;
                    var away = canvas.position - cam.transform.position;
                    if (away.sqrMagnitude > 0.0001f)
                        canvas.rotation = Quaternion.LookRotation(away, Vector3.up);
                }
            }
        }

        void RefreshLabel(ARAnchor anchor, Guide guide)
        {
            if (!guide.label || guide.saved) return;
            bool ready = IsReadyToSave(anchor);
            guide.label.text = (ready ? "Ready to save" : "Walk around the anchor") +
                               $"\n{guide.Coverage:P0} / {Mathf.Clamp01(minCoverage):P0}";
            guide.label.color = ready ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.85f, 0.2f);
        }

        void BuildRing(ARAnchor anchor, Guide guide)
        {
            var root = new GameObject($"AnchorQuality_{anchor.trackableId}");
            root.transform.SetParent(transform, false);
            root.transform.SetPositionAndRotation(anchor.transform.position, Quaternion.identity);
            guide.root = root.transform;
            guide.segments = new Renderer[guide.covered.Length];
            float radius = Mathf.Max(0.05f, ringRadius);
            float step = 360f / guide.covered.Length;
            for (int i = 0; i < guide.covered.Length; i++)
            {
                var segment = GameObject.CreatePrimitive(PrimitiveType.Cube);
                segment.name = $"Direction_{i}";
                var collider = segment.GetComponent<Collider>();
                collider.enabled = false;
                Destroy(collider);
                segment.transform.SetParent(root.transform, false);
                var rotation = Quaternion.Euler(0f, i * step, 0f);
                segment.transform.localPosition = rotation * Vector3.forward * radius;
                segment.transform.localRotation = rotation;
                segment.transform.localScale = new Vector3(radius * step * Mathf.Deg2Rad * 0.75f, 0.012f, 0.025f);
                var renderer = segment.GetComponent<Renderer>();
                if (!m_PendingMaterial)
                {
                    // Reuse the URP unlit shader already used by the scene's audio bars.
                    m_PendingMaterial = MakeMaterial(renderer.sharedMaterial, new Color(1f, 0.65f, 0.15f));
                    m_GoodMaterial = MakeMaterial(renderer.sharedMaterial, new Color(0.3f, 1f, 0.4f));
                }
                renderer.sharedMaterial = m_PendingMaterial;
                guide.segments[i] = renderer;
            }

            var canvasGo = new GameObject("QualityLabel", typeof(Canvas));
            canvasGo.transform.SetParent(root.transform, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)canvasGo.transform;
            rect.sizeDelta = new Vector2(600, 100);
            rect.localScale = Vector3.one * 0.0007f;
            rect.localPosition = new Vector3(0f, 0.12f, 0f);
            var textGo = new GameObject("Text", typeof(Text));
            textGo.transform.SetParent(rect, false);
            var textRect = (RectTransform)textGo.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            if (!m_Font) m_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            guide.label = textGo.GetComponent<Text>();
            guide.label.font = m_Font;
            guide.label.fontSize = 32;
            guide.label.alignment = TextAnchor.MiddleCenter;
            guide.label.raycastTarget = false;
            RefreshLabel(anchor, guide);
        }

        static Material MakeMaterial(Material source, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var material = shader ? new Material(shader) : new Material(source);
            material.color = color;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            return material;
        }

        string BuildDebug()
        {
            m_Text.Clear();
            m_Text.AppendLine($"<b>== ANCHOR QUALITY ==</b> GOOD coverage needed: {Mathf.Clamp01(minCoverage):P0}");
            foreach (var pair in m_Guides)
            {
                if (!pair.Key) continue;
                var guide = pair.Value;
                string quality = guide.sampled ? guide.quality.ToString().Replace("XREAL_ANCHOR_ESTIMATE_QUALITY_", "") : "not sampled";
                m_Text.AppendLine($"{pair.Key.trackableId.ToString().Substring(0, 8)}: coverage={guide.Coverage:P0} last={quality}" +
                                  (guide.saved ? " (saved)" : IsReadyToSave(pair.Key) ? " (ready)" : " (scan)"));
            }
            return m_Text.ToString();
        }

        void OnDestroy()
        {
            ClearGuides();
            if (m_PendingMaterial) Destroy(m_PendingMaterial);
            if (m_GoodMaterial) Destroy(m_GoodMaterial);
        }
    }
}
