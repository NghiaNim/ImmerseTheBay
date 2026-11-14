using System;
using System.Collections;
using System.Collections.Generic;
using ImmerseTheBay.Content;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ImmerseTheBay.Tracking
{
    /// <summary>
    /// Image tracking driven by the "markers" list in content.json: images are loaded at runtime into a mutable
    /// reference library (no editor asset needed), and when a marker is detected its content item
    /// (2D video, image or 3D model) is spawned on top of it and follows it.
    /// XREAL limits: up to 5 reference images, 1 tracked at a time.
    /// </summary>
    [RequireComponent(typeof(ARTrackedImageManager))]
    public class ImageTrackingController : MonoBehaviour
    {
        public ContentSpawner spawner;

        ARTrackedImageManager m_Manager;
        readonly Dictionary<TrackableId, PlaceableContent> m_Attached = new();
        readonly HashSet<TrackableId> m_Pending = new();
        string m_Status = "idle";
        Func<string> m_Debug;

        void Awake()
        {
            m_Manager = GetComponent<ARTrackedImageManager>();
            m_Manager.enabled = false; // enabled once the runtime library is ready
            if (!spawner) spawner = FindAnyObjectByType<ContentSpawner>();
            m_Debug = () => $"<b>== IMAGE TRACKING ==</b>\n{m_Status}\nlibrary={(m_Manager.referenceLibrary != null ? m_Manager.referenceLibrary.count : 0)} tracked={m_Manager.trackables.count} attached={m_Attached.Count}";
        }

        void OnEnable() => DebugRegistry.Register(m_Debug);
        void OnDisable() => DebugRegistry.Unregister(m_Debug);

        IEnumerator Start()
        {
            var load = ContentLibrary.LoadAsync();
            while (!load.IsCompleted) yield return null;
            var markers = load.Result.markers;
            if (markers == null || markers.Count == 0) { m_Status = "no markers in content.json"; yield break; }

            while (m_Manager.subsystem == null && ARSession.state < ARSessionState.Ready) yield return null;
            if (m_Manager.descriptor == null) { m_Status = "<color=#f88>image tracking not supported</color>"; yield break; }
            if (!m_Manager.descriptor.supportsMutableLibrary)
            {
                // XREAL: no runtime library. Use the XRReferenceImageLibrary asset assigned in the editor
                // (Assets/Settings/ReferenceImages.asset); image names must match "markers[].name" in content.json.
                if (m_Manager.referenceLibrary == null || m_Manager.referenceLibrary.count == 0)
                {
                    m_Status = "<color=#f88>add images to Assets/Settings/ReferenceImages.asset (XREAL has no runtime image library)</color>";
                    yield break;
                }
                m_Manager.enabled = true;
                m_Status = $"tracking {m_Manager.referenceLibrary.count} images from editor library";
                yield break;
            }

            var lib = m_Manager.CreateRuntimeLibrary() as MutableRuntimeReferenceImageLibrary;
            if (lib == null) { m_Status = "<color=#f88>could not create mutable library</color>"; yield break; }

            int added = 0;
            foreach (var m in markers)
            {
                m_Status = $"loading marker {m.name}...";
                using var req = UnityWebRequestTexture.GetTexture(ContentLibrary.ToUrl(m.image), nonReadable: false);
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { Debug.LogWarning($"[ImageTracking] {m.name}: {req.error}"); continue; }
                var tex = DownloadHandlerTexture.GetContent(req);
                var job = lib.ScheduleAddImageWithValidationJob(tex, m.name, m.widthMeters);
                while (!job.jobHandle.IsCompleted) yield return null;
                job.jobHandle.Complete();
                if (job.status == AddReferenceImageJobStatus.Success) added++;
                else Debug.LogWarning($"[ImageTracking] {m.name}: {job.status}");
            }

            m_Manager.referenceLibrary = lib;
            m_Manager.enabled = true;
            m_Status = $"tracking {added}/{markers.Count} markers";
        }

        void Update()
        {
            if (!m_Manager.enabled || ContentLibrary.Current == null) return;
            foreach (var img in m_Manager.trackables)
            {
                bool visible = img.trackingState == TrackingState.Tracking;
                if (m_Attached.TryGetValue(img.trackableId, out var c))
                {
                    if (c) c.gameObject.SetActive(visible);
                    continue;
                }
                if (!visible || m_Pending.Contains(img.trackableId)) continue;
                var marker = ContentLibrary.Current.markers.Find(m => m.name == img.referenceImage.name);
                var item = marker != null ? ContentLibrary.Find(marker.contentId) : null;
                if (item == null) continue;
                Attach(img, item);
            }
        }

        async void Attach(ARTrackedImage img, ContentItem item)
        {
            m_Pending.Add(img.trackableId);
            var c = await spawner.Spawn(item, default, img.transform);
            m_Pending.Remove(img.trackableId);
            if (!c) return;
            c.locked = true; // follows the image, not grabbable
            // Tracked image's up (Y) is the image normal; stand content on it facing the viewer.
            c.transform.localPosition = new Vector3(0, 0.01f, 0);
            c.transform.localRotation = Quaternion.Euler(90, 0, 0);
            spawner.Spawned.Remove(c); // not part of the saved room layout
            m_Attached[img.trackableId] = c;
            m_Status = $"attached {item.label} to {img.referenceImage.name}";
        }
    }
}
