using System;
using ImmerseTheBay.Tracking;
using UnityEngine;

namespace ImmerseTheBay.Content
{
    /// <summary>
    /// Bare-hand manipulation of <see cref="PlaceableContent"/>:
    ///  - Pinch near an object (or pinch while looking at it from afar) to grab: it follows the hand's position and rotation.
    ///  - Pinch the same object with the second hand to scale (hand distance) and yaw-rotate (hand axis) it.
    ///  - Release: the object is re-anchored at its new pose (<see cref="ContentSpawner.Reanchor"/>).
    /// </summary>
    public class HandManipulator : MonoBehaviour
    {
        public ContentSpawner spawner;
        // Enable in the inspector to use the old pinch path; XRI grabs are disabled while it is active.
        public bool useLegacyPinch = false;
        [Tooltip("Extra margin around an object's bounds that still counts as touching it (metres).")]
        public float nearMargin = 0.06f;
        public bool allowFarGrab = true;
        public float minScale = 0.05f, maxScale = 20f;

        struct Grab
        {
            public PlaceableContent target;
            public Vector3 posOffset;      // object position in hand space
            public Quaternion rotOffset;   // object rotation in hand space
        }

        Grab m_Left, m_Right;
        // two-hand state
        float m_StartDist, m_StartScale;
        Vector3 m_StartAxis;
        Quaternion m_StartRot;
        Vector3 m_StartMid, m_StartPos;
        PlaceableContent m_Hover;
        Func<string> m_Debug;

        void Awake()
        {
            if (!spawner) spawner = FindAnyObjectByType<ContentSpawner>();
            m_Debug = () => $"<b>== MANIPULATION ==</b>\nmode: {(useLegacyPinch ? "legacy pinch" : "XRI")}\nL grab: {Name(m_Left.target)}  R grab: {Name(m_Right.target)}  hover: {Name(m_Hover)}";
        }

        void OnEnable() => DebugRegistry.Register(m_Debug);
        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            ReleaseGrabs();
            SetLegacyInteraction(false);
        }
        static string Name(PlaceableContent c) => c ? c.item?.label ?? c.name : "-";

        void Update()
        {
            if (!useLegacyPinch)
            {
                ReleaseGrabs();
                SetLegacyInteraction(false);
                return;
            }
            SetLegacyInteraction(true);
            var hands = HandInput.Instance;
            if (!hands || !spawner) return;
            var l = hands.Left; var r = hands.Right;

            UpdateHover(l, r);
            HandleHand(l, ref m_Left, m_Right.target);
            HandleHand(r, ref m_Right, m_Left.target);

            bool twoHand = m_Left.target && m_Left.target == m_Right.target;
            if (twoHand) TwoHand(l, r, m_Left.target);
            else
            {
                if (m_Left.target) Follow(l, m_Left);
                if (m_Right.target) Follow(r, m_Right);
            }
        }

        void SetLegacyInteraction(bool legacy)
        {
            if (!spawner) return;
            foreach (var content in spawner.Spawned)
                if (content) content.SetLegacyInteraction(legacy);
        }

        void ReleaseGrabs()
        {
            var left = m_Left.target;
            var right = m_Right.target;
            m_Left = m_Right = default;
            if (m_Hover) m_Hover.SetHighlight(false);
            m_Hover = null;
            if (!spawner) return;
            if (left) spawner.Reanchor(left);
            if (right && right != left) spawner.Reanchor(right);
        }

        void HandleHand(HandState h, ref Grab g, PlaceableContent otherHandTarget)
        {
            if (h.pinchDown && !g.target)
            {
                var t = FindTarget(h);
                if (t && !t.locked)
                {
                    g.target = t;
                    var inv = Quaternion.Inverse(h.palmRotation);
                    g.posOffset = inv * (t.transform.position - h.pinchPoint);
                    g.rotOffset = inv * t.transform.rotation;
                    t.DetachForManipulation();
                    if (t == otherHandTarget) BeginTwoHand(t);
                }
            }
            else if ((h.pinchUp || !h.tracked) && g.target)
            {
                var t = g.target;
                g.target = null;
                bool stillHeld = t == m_Left.target || t == m_Right.target;
                if (stillHeld) RebaseOneHand(t);  // other hand keeps holding: recompute its offset
                else spawner.Reanchor(t);
            }
        }

        PlaceableContent FindTarget(HandState h)
        {
            PlaceableContent best = null;
            float bestD = float.MaxValue;
            foreach (var c in spawner.Spawned)
            {
                if (!c) continue;
                var b = c.GetBounds();
                b.Expand(nearMargin * 2);
                if (!b.Contains(h.pinchPoint)) continue;
                float d = (b.center - h.pinchPoint).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best || !allowFarGrab) return best;
            // Far grab: ray from the head through the pinch point
            var cam = Camera.main.transform;
            var ray = new Ray(cam.position, (h.pinchPoint - cam.position).normalized);
            foreach (var c in spawner.Spawned)
            {
                if (!c) continue;
                if (c.GetBounds().IntersectRay(ray, out float dist) && dist < bestD) { bestD = dist; best = c; }
            }
            return best;
        }

        void UpdateHover(HandState l, HandState r)
        {
            PlaceableContent hover = m_Left.target ? m_Left.target : m_Right.target;
            if (!hover && r.tracked) hover = FindTarget(r);
            if (!hover && l.tracked) hover = FindTarget(l);
            if (hover == m_Hover) { if (m_Hover) m_Hover.SetHighlight(true); return; }
            if (m_Hover) m_Hover.SetHighlight(false);
            m_Hover = hover;
            if (m_Hover) m_Hover.SetHighlight(true);
        }

        static void Follow(HandState h, Grab g)
        {
            if (!h.tracked) return;
            g.target.transform.SetPositionAndRotation(h.pinchPoint + h.palmRotation * g.posOffset, h.palmRotation * g.rotOffset);
        }

        void BeginTwoHand(PlaceableContent t)
        {
            var hands = HandInput.Instance;
            var l = hands.Left.pinchPoint; var r = hands.Right.pinchPoint;
            m_StartDist = Mathf.Max(0.01f, Vector3.Distance(l, r));
            m_StartScale = t.transform.localScale.x;
            m_StartAxis = Vector3.ProjectOnPlane(r - l, Vector3.up);
            m_StartRot = t.transform.rotation;
            m_StartMid = (l + r) * 0.5f;
            m_StartPos = t.transform.position;
        }

        void TwoHand(HandState l, HandState r, PlaceableContent t)
        {
            if (!l.tracked || !r.tracked) return;
            float s = Mathf.Clamp(m_StartScale * Vector3.Distance(l.pinchPoint, r.pinchPoint) / m_StartDist, minScale, maxScale);
            t.transform.localScale = Vector3.one * s;
            var axis = Vector3.ProjectOnPlane(r.pinchPoint - l.pinchPoint, Vector3.up);
            float yaw = Vector3.SignedAngle(m_StartAxis, axis, Vector3.up);
            t.transform.rotation = Quaternion.AngleAxis(yaw, Vector3.up) * m_StartRot;
            t.transform.position = m_StartPos + ((l.pinchPoint + r.pinchPoint) * 0.5f - m_StartMid);
        }

        void RebaseOneHand(PlaceableContent t)
        {
            var hands = HandInput.Instance;
            if (m_Left.target == t) Rebase(hands.Left, ref m_Left);
            if (m_Right.target == t) Rebase(hands.Right, ref m_Right);
        }

        static void Rebase(HandState h, ref Grab g)
        {
            var inv = Quaternion.Inverse(h.palmRotation);
            g.posOffset = inv * (g.target.transform.position - h.pinchPoint);
            g.rotOffset = inv * g.target.transform.rotation;
        }
    }
}
