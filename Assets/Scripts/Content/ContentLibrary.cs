using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ImmerseTheBay.Net;
using UnityEngine;

namespace ImmerseTheBay.Content
{
    [Serializable]
    public class ContentItem
    {
        public string id;
        public string label;
        /// <summary>video | image | model | primitive</summary>
        public string type;
        /// <summary>URL, absolute path, or path relative to StreamingAssets. For primitive: Cube / Sphere / Capsule / Cylinder.</summary>
        public string source;
        /// <summary>Mono | SideBySide | TopBottom (video / image only).</summary>
        public string layout = "Mono";
        /// <summary>Screen width (video / image) or largest dimension (model / primitive), metres.</summary>
        public float size = 0.5f;
    }

    [Serializable]
    public class MarkerItem
    {
        public string name;
        /// <summary>Image file (path relative to StreamingAssets, or URL).</summary>
        public string image;
        public float widthMeters = 0.2f;
        /// <summary>Content id spawned on top of the marker when it is detected.</summary>
        public string contentId;
    }

    [Serializable]
    public class ContentManifest
    {
        public List<ContentItem> items = new();
        public List<MarkerItem> markers = new();
    }

    /// <summary>Loads StreamingAssets/content.json (works inside the Android APK via UnityWebRequest).</summary>
    public static class ContentLibrary
    {
        static ContentManifest s_Manifest;
        static Task<ContentManifest> s_Loading;

        public static ContentManifest Current => s_Manifest;

        public static Task<ContentManifest> LoadAsync() => s_Loading ??= LoadInternal();

        static async Task<ContentManifest> LoadInternal()
        {
            try
            {
                var json = await NetClient.Get(ToUrl("content.json"));
                s_Manifest = JsonUtility.FromJson<ContentManifest>(json) ?? new ContentManifest();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Content] content.json not loaded ({e.Message}); using built-in defaults");
                s_Manifest = new ContentManifest
                {
                    items = { new ContentItem { id = "cube", label = "Cube", type = "primitive", source = "Cube", size = 0.15f } }
                };
            }
            return s_Manifest;
        }

        public static ContentItem Find(string id) => s_Manifest?.items.Find(i => i.id == id);

        /// <summary>Turns a manifest source into something UnityWebRequest / VideoPlayer can open.</summary>
        public static string ToUrl(string source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            if (source.Contains("://")) return source;
            var path = Path.IsPathRooted(source) ? source : Path.Combine(Application.streamingAssetsPath, source);
            return path.Contains("://") ? path : "file://" + path; // Android streamingAssetsPath is already jar:file://
        }
    }
}
