using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace ImmerseTheBay.Tracking
{
    /// <summary>Per-frame state of one hand in world space.</summary>
    public struct HandState
    {
        public bool tracked, pointing;
        public bool pinching, pinchDown, pinchUp;
        public float pinchCm;
        public Vector3 pinchPoint;   // midpoint of thumb tip and index tip
        public Vector3 indexTip;
        public Quaternion palmRotation;
    }

    /// <summary>
    /// Single source of truth for hand data (XR Hands via the XREAL hand subsystem), converted to world space.
    /// Other scripts read <see cref="Left"/> / <see cref="Right"/> instead of polling the subsystem themselves.
    /// Put it on the XR Origin's "Camera Offset" object (hand joints are reported in that space).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class HandInput : MonoBehaviour
    {
        public static HandInput Instance { get; private set; }
        public float pinchOnCm = 1.8f, pinchOffCm = 3.5f;

        public HandState Left => m_Left;
        public HandState Right => m_Right;
        public bool HasSubsystem => m_Subsystem != null;
        public HandState Get(Handedness h) => h == Handedness.Left ? m_Left : m_Right;

        HandState m_Left, m_Right;
        XRHandSubsystem m_Subsystem;
        static readonly List<XRHandSubsystem> s_List = new();

        void Awake() => Instance = this;

        void Update()
        {
            if (m_Subsystem == null || !m_Subsystem.running)
            {
                SubsystemManager.GetSubsystems(s_List);
                m_Subsystem = s_List.Find(s => s.running);
            }
            if (m_Subsystem == null) { m_Left = default; m_Right = default; return; }
            Read(m_Subsystem.leftHand, ref m_Left);
            Read(m_Subsystem.rightHand, ref m_Right);
        }

        void Read(XRHand hand, ref HandState s)
        {
            bool was = s.pinching;
            if (!hand.isTracked ||
                !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var t) ||
                !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var i))
            {
                s = default;
                s.pinchUp = was;
                return;
            }
            s.tracked = true;
            s.pinchCm = Vector3.Distance(t.position, i.position) * 100f;
            s.pinching = was ? s.pinchCm < pinchOffCm : s.pinchCm < pinchOnCm;
            s.pinchDown = s.pinching && !was;
            s.pinchUp = !s.pinching && was;
            s.indexTip = transform.TransformPoint(i.position);
            s.pinchPoint = transform.TransformPoint((t.position + i.position) * 0.5f);
            s.pointing = !s.pinching &&
                TryFingerBend(hand, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexTip, out float index) && index > 0.85f &&
                TryFingerBend(hand, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleTip, out float middle) && middle < 0.75f &&
                TryFingerBend(hand, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingTip, out float ring) && ring < 0.75f;
            s.palmRotation = hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var p)
                ? transform.rotation * p.rotation
                : transform.rotation * hand.rootPose.rotation;
        }

        static bool TryFingerBend(XRHand hand, XRHandJointID proximal, XRHandJointID intermediate, XRHandJointID tip, out float bend)
        {
            bend = 0f;
            if (!hand.GetJoint(proximal).TryGetPose(out var start) ||
                !hand.GetJoint(intermediate).TryGetPose(out var middle) || !hand.GetJoint(tip).TryGetPose(out var end))
                return false;
            var first = middle.position - start.position;
            var last = end.position - middle.position;
            if (first.sqrMagnitude < 0.000001f || last.sqrMagnitude < 0.000001f) return false;
            bend = Vector3.Dot(first.normalized, last.normalized);
            return true;
        }
    }
}
