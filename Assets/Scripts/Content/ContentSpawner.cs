using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImmerseTheBay.Media;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ImmerseTheBay.Content
{
    /// <summary>
    /// Spawns 2D video / image screens and 3D models (glTF/GLB via glTFast, or primitives) into the room,
    /// pins each one to its own ARAnchor, and saves / restores the whole layout across launches.
    /// </summary>
    public class ContentSpawner : MonoBehaviour
    {
        const string PrefsKey = "ImmerseTheBay.ContentLayout";

        public ARAnchorManager anchorManager;
        [Tooltip("Material using ImmerseTheBay/StereoUnlit, used for video / image screens.")]
        public Material screenMaterial;
        [Tooltip("Default material for primitives.")]
        public Material primitiveMaterial;
        public float spawnDistance = 0.8f;
        public bool restoreOnStart = true;

        public readonly List<PlaceableContent> Spawned = new();
        public event Action<PlaceableContent> OnSpawned;

        string m_Status = "idle";
        bool m_Restored, m_Busy;
        Func<string> m_Debug;

        void Awake()
        {
            if (!anchorManager) anchorManager = FindAnyObjectByType<ARAnchorManager>();
            m_Debug = () => $"<b>== CONTENT ==</b>\nitems in manifest={ContentLibrary.Current?.items.Count ?? 0} spawned={Spawned.Count}\nstatus: {m_Status}";
        }

        void OnEnable() => DebugRegistry.Register(m_Debug);
        void OnDisable() => DebugRegistry.Unregister(m_Debug);

        async void Start() => await ContentLibrary.LoadAsync();

        void Update()
        {
            if (restoreOnStart && !m_Restored && ARSession.state == ARSessionState.SessionTracking && anchorManager && anchorManager.subsystem != null)
            {
                m_Restored = true;
                RestoreLayout();
            }
        }

        // ------------------------------------------------------------ spawning
        /// <summary>Spawns the item in front of the user's head and anchors it.</summary>
        public async void SpawnInFront(ContentItem item)
        {
            var cam = Camera.main.transform;
            var fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            var pos = cam.position + cam.forward * spawnDistance;
            var c = await Spawn(item, new Pose(pos, Quaternion.LookRotation(fwd, Vector3.up)), null);
            if (c) Reanchor(c);
        }

        /// <summary>Creates the content at <paramref name="pose"/> (world) or under <paramref name="parent"/> (local identity).</summary>
        public async Task<PlaceableContent> Spawn(ContentItem item, Pose pose, Transform parent, float scale = 1f)
        {
            if (item == null) return null;
            var root = new GameObject($"Content_{item.id}");
            if (parent) { root.transform.SetParent(parent, false); }
            else root.transform.SetPositionAndRotation(pose.position, pose.rotation);
            root.transform.localScale = Vector3.one * scale;
            var content = root.AddComponent<PlaceableContent>();
            content.item = item;
            m_Status = $"loading {item.label}...";

            try
            {
                switch (item.type)
                {
                    case "video":
                    case "image": BuildScreen(root.transform, item); break;
                    case "model": await BuildModel(root.transform, item); break;
                    default: BuildPrimitive(root.transform, item); break;
                }
            }
            catch (Exception e)
            {
                m_Status = $"<color=#f88>{item.label} failed: {e.Message}</color>";
                Destroy(root);
                return null;
            }

            content.RefreshRenderers();
            Spawned.Add(content);
            m_Status = $"spawned {item.label}";
            OnSpawned?.Invoke(content);
            return content;
        }

        void BuildScreen(Transform root, ContentItem item)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Screen";
            Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(root, false);
            // Quad's front is visible looking along +Z, which is the root's forward (away from the user): no rotation needed.
            quad.transform.localScale = new Vector3(item.size, item.size * 9f / 16f, 1);
            var mr = quad.GetComponent<MeshRenderer>();
            if (screenMaterial) mr.sharedMaterial = screenMaterial;
            var player = quad.AddComponent<SpatialMediaPlayer>();
            player.widthMeters = item.size;
            player.playOnStart = false;
            if (!Enum.TryParse(item.layout, out StereoLayout layout)) layout = StereoLayout.Mono;
            player.layout = layout;
            if (!string.IsNullOrEmpty(item.source)) player.Play(item.source, layout);
        }

        static async Task BuildModel(Transform root, ContentItem item)
        {
            var holder = new GameObject("Model").transform;
            holder.SetParent(root, false);
            var gltf = new GLTFast.GltfImport();
            bool ok = await gltf.Load(ContentLibrary.ToUrl(item.source));
            if (!ok) throw new Exception("glTF load failed");
            ok = await gltf.InstantiateMainSceneAsync(holder);
            if (!ok) throw new Exception("glTF instantiate failed");
            if (!root) return; // destroyed while loading
            root.gameObject.AddComponent<GltfOwner>().import = gltf;
            FitToSize(root, holder, item.size);
        }

        void BuildPrimitive(Transform root, ContentItem item)
        {
            if (!Enum.TryParse(item.source, true, out PrimitiveType type)) type = PrimitiveType.Cube;
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(root, false);
            go.transform.localScale = Vector3.one * item.size;
            if (primitiveMaterial) go.GetComponent<Renderer>().sharedMaterial = primitiveMaterial;
        }

        /// <summary>Scales and centres the model so its largest dimension equals <paramref name="size"/> and it sits on the root.</summary>
        static void FitToSize(Transform root, Transform holder, float size)
        {
            var renderers = holder.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            var b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            float max = Mathf.Max(b.size.x, b.size.y, b.size.z);
            if (max < 1e-5f) return;
            float s = size / max;
            holder.localScale *= s;
            var centreLocal = root.InverseTransformPoint(b.center);
            holder.localPosition -= centreLocal * s;
        }

        public void Delete(PlaceableContent c)
        {
            if (!c) return;
            Spawned.Remove(c);
            if (c.anchor && anchorManager) anchorManager.TryRemoveAnchor(c.anchor);
            Destroy(c.gameObject);
        }

        public void DeleteLast()
        {
            if (Spawned.Count > 0) Delete(Spawned[^1]);
        }

        // ------------------------------------------------------------ anchoring
        /// <summary>Replaces the content's anchor with a new one at its current pose (call after moving it).</summary>
        public async void Reanchor(PlaceableContent c)
        {
            if (!c || !anchorManager || anchorManager.subsystem == null) return;
            var pose = new Pose(c.transform.position, c.transform.rotation);
            var scale = c.transform.lossyScale;
            var old = c.anchor;
            c.transform.SetParent(null, true);
            if (old) anchorManager.TryRemoveAnchor(old);
            var r = await anchorManager.TryAddAnchorAsync(pose);
            if (!c) return;
            if (!r.status.IsSuccess()) { c.anchor = null; m_Status = $"anchor failed: {r.status}"; return; }
            c.anchor = r.value;
            c.transform.SetParent(r.value.transform, false);
            c.transform.localPosition = Vector3.zero;
            c.transform.localRotation = Quaternion.identity;
            c.transform.localScale = scale;
        }

        // ------------------------------------------------------------ persistence
        [Serializable] class Record { public string guid; public string itemId; public float scale; }
        [Serializable] class Layout { public List<Record> records = new(); }

        public async void SaveLayout()
        {
            if (m_Busy || !anchorManager || anchorManager.subsystem == null)
            { m_Status = "anchor subsystem not running"; return; }
            m_Busy = true;
            await EraseStored();
            var layout = new Layout();
            foreach (var c in Spawned.ToArray())
            {
                if (!c || !c.anchor) continue;
                m_Status = $"saving {c.item.label}... (look around)";
                var r = await anchorManager.TrySaveAnchorAsync(c.anchor);
                if (!r.status.IsSuccess()) continue;
                layout.records.Add(new Record { guid = GuidToString(r.value), itemId = c.item.id, scale = c.transform.localScale.x });
            }
            PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(layout));
            PlayerPrefs.Save();
            m_Status = $"saved {layout.records.Count}/{Spawned.Count} items";
            m_Busy = false;
        }

        public async void RestoreLayout()
        {
            var json = PlayerPrefs.GetString(PrefsKey, "");
            if (string.IsNullOrEmpty(json) || anchorManager.subsystem == null) return;
            await ContentLibrary.LoadAsync();
            var layout = JsonUtility.FromJson<Layout>(json);
            m_Busy = true;
            int ok = 0;
            foreach (var rec in layout.records)
            {
                var item = ContentLibrary.Find(rec.itemId);
                if (item == null) continue;
                m_Status = $"restoring {ok}/{layout.records.Count}... (look around the room)";
                var r = await anchorManager.TryLoadAnchorAsync(StringToGuid(rec.guid));
                if (!r.status.IsSuccess()) continue;
                var c = await Spawn(item, default, r.value.transform, rec.scale <= 0 ? 1 : rec.scale);
                if (c) { c.anchor = r.value; ok++; }
            }
            m_Status = $"restored {ok}/{layout.records.Count} items";
            m_Busy = false;
        }

        public async void ClearAll()
        {
            await EraseStored();
            PlayerPrefs.DeleteKey(PrefsKey);
            foreach (var c in Spawned.ToArray()) Delete(c);
            m_Status = "cleared";
        }

        async Task EraseStored()
        {
            var json = PlayerPrefs.GetString(PrefsKey, "");
            if (string.IsNullOrEmpty(json) || anchorManager.subsystem == null) return;
            foreach (var rec in JsonUtility.FromJson<Layout>(json).records)
                await anchorManager.TryEraseAnchorAsync(StringToGuid(rec.guid));
        }

        static string GuidToString(SerializableGuid g) => ImmerseTheBay.Anchors.GuidCodec.Encode(g);
        static SerializableGuid StringToGuid(string s) => ImmerseTheBay.Anchors.GuidCodec.TryDecode(s, out var g) ? g : default;
    }

    /// <summary>Keeps the glTF import alive (textures / meshes) and disposes it with the object.</summary>
    public class GltfOwner : MonoBehaviour
    {
        public GLTFast.GltfImport import;
        void OnDestroy() => import?.Dispose();
    }
}
