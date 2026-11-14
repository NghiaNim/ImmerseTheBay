using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Hands;
using Unity.XR.XREAL;
using Keyboard = UnityEngine.InputSystem.Keyboard;

namespace ImmerseTheBay.Anchors
{
    /// <summary>
    /// Spatial anchor workflow on top of AR Foundation 6 persistent anchors (implemented by XREAL's XREALAnchorSubsystem):
    ///  - Place:   right-hand pinch (at index tip) / phone-screen tap / Space key (1 m in front of head)
    ///  - Save:    left-hand pinch / S key  -> every unsaved anchor is persisted, GUIDs stored in PlayerPrefs
    ///  - Restore: automatic on start once tracking is up (re-localises saved anchors in the scanned space)
    ///  - Clear:   hold left pinch for 2 s / C key -> erase all saved anchors and remove them from the scene
    /// Content you parent under an anchor's transform stays locked to that real-world spot across app launches.
    /// </summary>
    [RequireComponent(typeof(ARAnchorManager))]
    public class SpatialAnchorController : MonoBehaviour
    {
        const string PrefsKey = "ImmerseTheBay.SavedAnchors";

        [Tooltip("Optional visual spawned under each anchor. A small cube is used if empty.")]
        public GameObject anchorVisualPrefab;
        public float pinchOnCm = 1.8f, pinchOffCm = 3.5f;
        public float clearHoldSeconds = 2f;
        public bool restoreOnStart = true;
        [Tooltip("Pinch gestures for anchors. Off by default because pinch is used to grab content (HandManipulator).")]
        public bool useHandGestures = false;

        public event Action<ARAnchor, SerializableGuid> AnchorRestored;
        public IReadOnlyDictionary<ARAnchor, SerializableGuid> Saved => m_Saved;

        ARAnchorManager m_Manager;
        XRHandSubsystem m_Hands;
        Transform m_TrackingSpace;
        readonly Dictionary<ARAnchor, SerializableGuid> m_Saved = new();
        readonly List<ARAnchor> m_Unsaved = new();
        bool m_RightPinch, m_LeftPinch, m_Busy, m_Restored;
        float m_LeftPinchStart;
        string m_Status = "idle";
        Func<string> m_Debug;

        void Awake()
        {
            m_Manager = GetComponent<ARAnchorManager>();
            var origin = GetComponentInParent<Unity.XR.CoreUtils.XROrigin>();
            m_TrackingSpace = origin && origin.CameraFloorOffsetObject ? origin.CameraFloorOffsetObject.transform : transform;
            m_Debug = BuildDebug;
        }

        void OnEnable() => DebugRegistry.Register(m_Debug);
        void OnDisable() => DebugRegistry.Unregister(m_Debug);

        string BuildDebug()
        {
            var d = m_Manager.descriptor;
            string caps = d == null ? "no subsystem" :
                $"subsystem={d.id}";
            return $"<b>== SPATIAL ANCHORS ==</b>\n{caps}\nstatus: {m_Status}\n" +
                   $"in scene={m_Manager.trackables.count} saved={m_Saved.Count} unsaved={m_Unsaved.Count} stored ids={LoadIds().Count}\n" +
                   (useHandGestures ? "R-pinch/Space=place  L-pinch/S=save  hold L-pinch/C=clear" : "Space=place  S=save  C=clear  M=remap");
        }

        void Update()
        {
            if (restoreOnStart && !m_Restored && ARSession.state == ARSessionState.SessionTracking && m_Manager.subsystem != null)
            {
                m_Restored = true;
                RestoreAll();
            }
            PollInput();
        }

        // ------------------------------------------------------------ input
        void PollInput()
        {
            if (m_Hands == null || !m_Hands.running)
            {
                var list = new List<XRHandSubsystem>();
                SubsystemManager.GetSubsystems(list);
                m_Hands = list.FirstOrDefault(s => s.running);
            }

            if (useHandGestures && m_Hands != null)
            {
                if (Pinch(m_Hands.rightHand, ref m_RightPinch, out var tip, out bool rightDown) && rightDown)
                    PlaceAt(m_TrackingSpace.TransformPoint(tip));

                bool wasLeft = m_LeftPinch;
                Pinch(m_Hands.leftHand, ref m_LeftPinch, out _, out bool leftDown);
                if (leftDown) m_LeftPinchStart = Time.time;
                if (m_LeftPinch && Time.time - m_LeftPinchStart > clearHoldSeconds && m_LeftPinchStart > 0)
                {
                    m_LeftPinchStart = -1; // consumed
                    ClearAll();
                }
                if (wasLeft && !m_LeftPinch && m_LeftPinchStart > 0) SaveAll(); // short pinch released
            }
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.spaceKey.wasPressedThisFrame) PlaceInFront();
                if (kb.sKey.wasPressedThisFrame) SaveAll();
                if (kb.cKey.wasPressedThisFrame) ClearAll();
                if (kb.mKey.wasPressedThisFrame) RemapAll();
            }
        }

        /// <summary>Hysteresis pinch detector. Returns true while the hand is tracked; <paramref name="down"/> is the rising edge.</summary>
        bool Pinch(XRHand hand, ref bool state, out Vector3 indexTip, out bool down)
        {
            indexTip = default; down = false;
            if (!hand.isTracked ||
                !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var t) ||
                !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var i)) { state = false; return false; }
            indexTip = i.position;
            float cm = Vector3.Distance(t.position, i.position) * 100f;
            bool now = state ? cm < pinchOffCm : cm < pinchOnCm;
            down = now && !state;
            state = now;
            return true;
        }

        // ------------------------------------------------------------ actions
        public void PlaceInFront()
        {
            var cam = Camera.main;
            if (cam) PlaceAt(cam.transform.position + cam.transform.forward * 1f);
        }

        public async void PlaceAt(Vector3 worldPos)
        {
            var cam = Camera.main;
            var fwd = cam ? Vector3.ProjectOnPlane(worldPos - cam.transform.position, Vector3.up) : Vector3.forward;
            var pose = new Pose(worldPos, Quaternion.LookRotation(fwd.sqrMagnitude > 1e-4f ? fwd : Vector3.forward, Vector3.up));
            var result = await m_Manager.TryAddAnchorAsync(pose);
            if (!result.status.IsSuccess()) { m_Status = $"<color=#f88>add failed: {result.status}</color>"; return; }
            var anchor = result.value;
            AttachVisual(anchor, saved: false);
            m_Unsaved.Add(anchor);
            m_Status = $"placed {Short(anchor.trackableId)} (unsaved)";
        }

        public async void SaveAll()
        {
            if (m_Busy || m_Unsaved.Count == 0) return;
            if (m_Manager.subsystem == null) { m_Status = "<color=#f88>anchor subsystem not running</color>"; m_Busy = false; return; }
            m_Busy = true;
            int ok = 0;
            foreach (var anchor in m_Unsaved.ToArray())
            {
                if (!anchor) { m_Unsaved.Remove(anchor); continue; }
                m_Status = $"saving {Short(anchor.trackableId)}... (keep looking around)";
                var r = await m_Manager.TrySaveAnchorAsync(anchor);
                if (!r.status.IsSuccess()) { m_Status = $"<color=#f88>save failed: {r.status}</color>"; continue; }
                m_Unsaved.Remove(anchor);
                m_Saved[anchor] = r.value;
                SetVisualColor(anchor, saved: true);
                ok++;
            }
            StoreIds();
            m_Status = $"saved {ok} anchor(s)";
            m_Busy = false;
        }

        public async void RestoreAll()
        {
            if (m_Manager.subsystem == null) { m_Status = "anchor subsystem not running"; return; }
            var ids = LoadIds();
            if (ids.Count == 0) { m_Status = "no saved anchors"; return; }
            m_Busy = true;
            int ok = 0;
            foreach (var id in ids)
            {
                m_Status = $"restoring {ok}/{ids.Count}... (look around the room)";
                var r = await m_Manager.TryLoadAnchorAsync(id);
                if (!r.status.IsSuccess()) continue;
                m_Saved[r.value] = id;
                AttachVisual(r.value, saved: true);
                AnchorRestored?.Invoke(r.value, id);
                ok++;
            }
            m_Status = $"restored {ok}/{ids.Count}";
            m_Busy = false;
        }

        public async void ClearAll()
        {
            if (m_Busy) return;
            m_Busy = true;
            bool canErase = m_Manager.subsystem != null; // XREAL implements erase without setting descriptor flags
            foreach (var id in LoadIds())
                if (canErase) await m_Manager.TryEraseAnchorAsync(id);
            foreach (var a in m_Saved.Keys.Concat(m_Unsaved).ToArray())
                if (a) m_Manager.TryRemoveAnchor(a);
            m_Saved.Clear(); m_Unsaved.Clear();
            PlayerPrefs.DeleteKey(PrefsKey);
            m_Status = "cleared all anchors";
            m_Busy = false;
        }

        /// <summary>XREAL-only: re-localise every anchor in the scene against the current map (fixes drift after relocalisation).</summary>
        public void RemapAll()
        {
            int ok = 0, total = 0;
            foreach (var a in m_Manager.trackables) { total++; if (m_Manager.TryRemap(a.trackableId)) ok++; }
            m_Status = $"remapped {ok}/{total}";
        }

        // ------------------------------------------------------------ persistence of GUID list
        [Serializable] class IdList { public List<string> ids = new(); }

        void StoreIds()
        {
            var list = new IdList();
            foreach (var g in m_Saved.Values) list.ids.Add(GuidCodec.Encode(g));
            PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(list));
            PlayerPrefs.Save();
        }

        static List<SerializableGuid> LoadIds()
        {
            var result = new List<SerializableGuid>();
            var json = PlayerPrefs.GetString(PrefsKey, "");
            if (string.IsNullOrEmpty(json)) return result;
            foreach (var s in JsonUtility.FromJson<IdList>(json).ids)
                if (GuidCodec.TryDecode(s, out var g))
                    result.Add(g);
            return result;
        }

        // ------------------------------------------------------------ visuals
        void AttachVisual(ARAnchor anchor, bool saved)
        {
            GameObject v;
            if (anchorVisualPrefab) v = Instantiate(anchorVisualPrefab, anchor.transform);
            else
            {
                v = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(v.GetComponent<Collider>());
                v.transform.SetParent(anchor.transform, false);
                v.transform.localScale = Vector3.one * 0.05f;
            }
            v.name = "AnchorVisual";
            SetVisualColor(anchor, saved);
        }

        static void SetVisualColor(ARAnchor anchor, bool saved)
        {
            var r = anchor.GetComponentInChildren<Renderer>();
            if (!r) return;
            var c = saved ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.85f, 0.2f);
            var mat = r.material;
            mat.color = c;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
        }

        static string Short(TrackableId id) => id.ToString().Substring(0, 8);
    }
}
