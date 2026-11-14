using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    ///  - Save:    left-hand pinch / S key -> persist once the quality guide has enough GOOD viewpoints
    ///  - Restore: automatic on start once tracking is up (re-localises saved anchors in the scanned space)
    ///  - Clear:   hold left pinch for 2 s / C key -> erase all saved anchors and remove them from the scene
    /// Content you parent under an anchor's transform stays locked to that real-world spot across app launches.
    /// </summary>
    [RequireComponent(typeof(ARAnchorManager))]
    public class SpatialAnchorController : MonoBehaviour
    {
        const string PrefsKey = "ImmerseTheBay.SavedAnchors";
        const float RestoreRemapStableSeconds = 2f;

        [Tooltip("Optional visual spawned under each anchor. A small cube is used if empty.")]
        public GameObject anchorVisualPrefab;
        public float pinchOnCm = 1.8f, pinchOffCm = 3.5f;
        public float clearHoldSeconds = 2f;
        public bool restoreOnStart = true;
        [Tooltip("Pinch gestures for anchors. Off by default because pinch is used to grab content (HandManipulator).")]
        public bool useHandGestures = false;

        public event Action<ARAnchor> AnchorPlaced;
        public event Action<ARAnchor, SerializableGuid> AnchorRestored;
        public IReadOnlyDictionary<ARAnchor, SerializableGuid> Saved => m_Saved;
        public IReadOnlyList<ARAnchor> Unsaved => m_Unsaved;

        ARAnchorManager m_Manager;
        AnchorQualityGuide m_QualityGuide;
        XRHandSubsystem m_Hands;
        Transform m_TrackingSpace;
        readonly Dictionary<ARAnchor, SerializableGuid> m_Saved = new();
        readonly List<ARAnchor> m_Unsaved = new();
        sealed class RestoreRemap
        {
            public float stableSince = -1f;
            public bool attempted;
            public string result = "waiting for stable tracking";
        }
        readonly Dictionary<ARAnchor, RestoreRemap> m_RestoreRemaps = new();
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
            // Existing generated scenes only add this controller. No scene/builder change is needed.
            m_QualityGuide = GetComponent<AnchorQualityGuide>();
            if (!m_QualityGuide) m_QualityGuide = gameObject.AddComponent<AnchorQualityGuide>();
        }

        void OnEnable() => DebugRegistry.Register(m_Debug);
        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            ResetRemapStability();
        }

        void OnApplicationPause(bool paused)
        {
            if (paused) ResetRemapStability();
        }

        void ResetRemapStability()
        {
            // Time spent disabled or in the background does not count as observed tracking.
            foreach (var remap in m_RestoreRemaps.Values) remap.stableSince = -1f;
        }

        string BuildDebug()
        {
            var d = m_Manager.descriptor;
            string caps = d == null ? "no subsystem" :
                $"subsystem={d.id}";
            var text = new StringBuilder($"<b>== SPATIAL ANCHORS ==</b>\n{caps}\nstatus: {m_Status}\n" +
                   $"in scene={m_Manager.trackables.count} saved={m_Saved.Count} unsaved={m_Unsaved.Count} stored ids={LoadIds().Count}\n" +
                   (useHandGestures ? "R-pinch/Space=place  L-pinch/S=save  hold L-pinch/C=clear" : "Space=place  S=save  C=clear  M=remap"));
            foreach (var pair in m_RestoreRemaps)
                if (pair.Key) text.Append($"\nrestore remap {Short(pair.Key.trackableId)}: {pair.Value.result}");
            return text.ToString();
        }

        void Update()
        {
            if (restoreOnStart && !m_Restored && !m_Busy && ARSession.state == ARSessionState.SessionTracking && m_Manager.subsystem != null)
            {
                m_Restored = true;
                RestoreAll();
            }
            UpdateRestoreRemaps();
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
            if (m_Busy) return;
            if (m_Manager.subsystem == null) { m_Status = "anchor subsystem not running"; return; }
            m_Busy = true;
            try
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
                AnchorPlaced?.Invoke(anchor);
            }
            catch (Exception e) { m_Status = $"<color=#f88>place failed: {e.Message}</color>"; }
            finally { m_Busy = false; }
        }

        public void SaveAll() => SaveAll(false);

        /// <summary>Warn and leave anchors unsaved until coverage is sufficient. Explicit callers may bypass with forceSave.</summary>
        public async void SaveAll(bool forceSave)
        {
            if (m_Busy) return;
            m_Unsaved.RemoveAll(anchor => !anchor);
            if (m_Unsaved.Count == 0) return;
            if (m_Manager.subsystem == null) { m_Status = "<color=#f88>anchor subsystem not running</color>"; return; }
            // Preflight the entire set so one Save action never silently skips an under-scanned anchor.
            if (!forceSave)
            {
                foreach (var anchor in m_Unsaved)
                {
                    if (m_QualityGuide && m_QualityGuide.IsReadyToSave(anchor)) continue;
                    m_Status = m_QualityGuide && m_QualityGuide.isActiveAndEnabled
                        ? $"<color=#ffca66>save blocked: {Short(anchor.trackableId)} coverage {m_QualityGuide.GetCoverage(anchor):P0}/{m_QualityGuide.minCoverage:P0}. Walk around the anchor, then save again.</color>"
                        : "<color=#ffca66>save blocked: enable AnchorQualityGuide or explicitly call SaveAll(true).</color>";
                    return;
                }
            }
            m_Busy = true;
            int ok = 0, failed = 0, total = m_Unsaved.Count;
            string lastFailure = "";
            try
            {
                foreach (var anchor in m_Unsaved.ToArray())
                {
                    if (!anchor) { m_Unsaved.Remove(anchor); continue; }
                    m_Status = $"saving {Short(anchor.trackableId)}... (keep looking around)";
                    var r = await m_Manager.TrySaveAnchorAsync(anchor);
                    if (!r.status.IsSuccess()) { failed++; lastFailure = r.status.ToString(); continue; }
                    m_Unsaved.Remove(anchor);
                    m_Saved[anchor] = r.value;
                    SetVisualColor(anchor, saved: true);
                    if (m_QualityGuide) m_QualityGuide.MarkSaved(anchor);
                    ok++;
                }
                StoreIds();
                m_Status = $"saved {ok}/{total} anchor(s)" + (forceSave ? " (forced)" : "") +
                           (failed > 0 ? $"; <color=#f88>{failed} failed: {lastFailure}</color>" : "");
            }
            catch (Exception e)
            {
                StoreIds(); // Retain successful saves even if a later anchor throws.
                m_Status = $"<color=#f88>save interrupted after {ok}/{total}: {e.Message}</color>";
            }
            finally { m_Busy = false; }
        }

        public async void RestoreAll()
        {
            if (m_Busy) return;
            if (m_Manager.subsystem == null) { m_Status = "anchor subsystem not running"; return; }
            var ids = LoadIds();
            if (ids.Count == 0) { m_Status = "no saved anchors"; return; }
            m_Busy = true;
            int ok = 0;
            try
            {
                foreach (var id in ids)
                {
                    if (m_Saved.Any(pair => pair.Key && pair.Value.Equals(id))) { ok++; continue; }
                    m_Status = $"restoring {ok}/{ids.Count}... (look around the room)";
                    var r = await m_Manager.TryLoadAnchorAsync(id);
                    if (!r.status.IsSuccess()) continue;
                    m_Saved[r.value] = id;
                    m_RestoreRemaps[r.value] = new RestoreRemap();
                    AttachVisual(r.value, saved: true);
                    AnchorRestored?.Invoke(r.value, id);
                    ok++;
                }
                m_Status = $"restored {ok}/{ids.Count}";
            }
            catch (Exception e) { m_Status = $"<color=#f88>restore interrupted after {ok}/{ids.Count}: {e.Message}</color>"; }
            finally { m_Busy = false; }
        }

        public async void ClearAll()
        {
            if (m_Busy) return;
            m_Busy = true;
            try
            {
                bool canErase = m_Manager.subsystem != null; // XREAL implements erase without setting descriptor flags
                foreach (var id in LoadIds())
                    if (canErase) await m_Manager.TryEraseAnchorAsync(id);
                foreach (var a in m_Saved.Keys.Concat(m_Unsaved).ToArray())
                    if (a) m_Manager.TryRemoveAnchor(a);
                m_Saved.Clear(); m_Unsaved.Clear();
                m_RestoreRemaps.Clear();
                if (m_QualityGuide) m_QualityGuide.ClearGuides();
                PlayerPrefs.DeleteKey(PrefsKey);
                m_Status = "cleared all anchors";
            }
            catch (Exception e) { m_Status = $"<color=#f88>clear failed: {e.Message}</color>"; }
            finally { m_Busy = false; }
        }

        void UpdateRestoreRemaps()
        {
            bool sessionTracking = ARSession.state == ARSessionState.SessionTracking && m_Manager.isActiveAndEnabled &&
                                   m_Manager.subsystem != null && m_Manager.subsystem.running;
            foreach (var pair in m_RestoreRemaps)
            {
                var remap = pair.Value;
                if (remap.attempted) continue;
                if (!pair.Key)
                {
                    remap.attempted = true;
                    remap.result = "anchor removed";
                    continue;
                }
                if (!sessionTracking || pair.Key.trackingState != TrackingState.Tracking)
                {
                    remap.stableSince = -1f;
                    remap.result = "waiting for stable tracking";
                    continue;
                }
                if (remap.stableSince < 0f) remap.stableSince = Time.unscaledTime;
                float stableFor = Time.unscaledTime - remap.stableSince;
                remap.result = $"tracking {Mathf.Min(stableFor, RestoreRemapStableSeconds):F1}/{RestoreRemapStableSeconds:F1}s";
                if (stableFor < RestoreRemapStableSeconds || m_Busy) continue;
                // One automatic attempt per restored anchor, including failures. M remains an explicit retry.
                remap.attempted = true;
                try { remap.result = m_Manager.TryRemap(pair.Key.trackableId) ? "succeeded" : "failed (M to retry)"; }
                catch (Exception e) { remap.result = $"failed: {e.Message} (M to retry)"; }
            }
        }

        /// <summary>XREAL-only: re-localise every anchor in the scene against the current map (fixes drift after relocalisation).</summary>
        public void RemapAll()
        {
            if (m_Busy) return;
            if (m_Manager.subsystem == null) { m_Status = "anchor subsystem not running"; return; }
            int ok = 0, total = 0;
            foreach (var a in m_Manager.trackables)
            {
                total++;
                bool remapped = m_Manager.TryRemap(a.trackableId);
                if (remapped) ok++;
                if (m_RestoreRemaps.TryGetValue(a, out var remap) && remap.attempted)
                    remap.result = remapped ? "manual retry succeeded" : "manual retry failed (M to retry)";
            }
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
