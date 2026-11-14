using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace ImmerseTheBay.Media
{
    public enum StereoLayout { Mono = 0, SideBySide = 1, TopBottom = 2 }
    public enum MediaProjection { Flat = 0, Dome180 = 1, Sphere360 = 2 }

    /// <summary>
    /// Shows an image or video on a quad or an inward-facing panorama, in mono or stereo.
    /// Source can be an http(s) URL, an absolute file path, or a path relative to StreamingAssets.
    /// The renderer must use a material with the ImmerseTheBay/StereoUnlit shader.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    public class SpatialMediaPlayer : MonoBehaviour
    {
        [Tooltip("URL, absolute path, or path relative to StreamingAssets. Leave empty to load later via Play().")]
        public string source;
        public StereoLayout layout = StereoLayout.SideBySide;
        public MediaProjection projection = MediaProjection.Flat;
        [Tooltip("Radius of immersive media in metres. Its centre is the head position when Play is called.")]
        [Min(0.1f)] public float radiusMeters = 10f;
        public bool swapEyes;
        [Tooltip("Width of the screen in metres; height follows the media aspect ratio.")]
        public float widthMeters = 1.2f;
        public bool loop = true;
        public bool playOnStart = true;
        [Tooltip("Video audio is routed here so the waveform visualiser can see it.")]
        public AudioSource audioSource;

        public string Status { get; private set; } = "idle";
        public VideoPlayer Video { get; private set; }
        public bool IsPlaying => m_IsVideo && Video && Video.isPrepared && Video.isPlaying;
        public double Duration => m_IsVideo && Video && Video.isPrepared ? Video.length : 0;
        public double Time => m_IsVideo && Video && Video.isPrepared ? Video.time : 0;

        static readonly int s_Layout = Shader.PropertyToID("_Layout");
        static readonly int s_Swap = Shader.PropertyToID("_SwapEyes");
        static readonly int s_Projection = Shader.PropertyToID("_Projection");
        Material m_Mat;
        Texture2D m_Image;
        MeshFilter m_Filter;
        Mesh m_FlatMesh, m_PanoramaMesh;
        Vector3 m_FlatPosition, m_FlatScale;
        Quaternion m_FlatRotation;
        bool m_IsVideo, m_PauseRequested;
        double? m_PendingSeek;
        Func<string> m_Debug;

        void Awake()
        {
            m_Mat = GetComponent<MeshRenderer>().material;
            m_Filter = GetComponent<MeshFilter>();
            m_FlatMesh = m_Filter.sharedMesh;
            if (!audioSource) audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            m_Debug = () => $"<b>== MEDIA ==</b>\n{Status}\n{projection} / {layout}, swap={swapEyes}, volume={audioSource.volume:F2}" +
                (m_IsVideo && Video && Video.isPrepared ? $"\nvideo {Video.width}x{Video.height} t={Time:F1}/{Duration:F1}s " +
                    (IsPlaying ? "playing" : m_PauseRequested ? "paused" : "stopped / ended") : "");
        }

        void OnEnable() => DebugRegistry.Register(m_Debug);
        void OnDisable() => DebugRegistry.Unregister(m_Debug);

        void Start()
        {
            if (playOnStart && !string.IsNullOrEmpty(source)) Play(source, layout, projection);
            else if (string.IsNullOrEmpty(source)) Status = "no source set";
        }

        /// <summary>Plays on a flat screen, preserving the original API's behaviour.</summary>
        public void Play(string src, StereoLayout stereo) => Play(src, stereo, MediaProjection.Flat);

        public void Play(string src, StereoLayout layout, MediaProjection projection)
        {
            StopAllCoroutines();
            m_IsVideo = false;
            if (Video) Video.Stop();
            source = src; this.layout = layout; this.projection = projection;
            m_PauseRequested = false;
            m_PendingSeek = null;
            m_Mat.mainTexture = null;
            if (m_Image) { Destroy(m_Image); m_Image = null; }
            SetProjection();
            if (string.IsNullOrWhiteSpace(src)) { Status = "no source set"; return; }
            string url = Resolve(src);
            string ext = Path.GetExtension(src.Split('?')[0]).ToLowerInvariant();
            bool isImage = ext is ".png" or ".jpg" or ".jpeg";
            if (isImage) StartCoroutine(LoadImage(url));
            else PlayVideo(url);
        }

        /// <summary>May also be called while the video is preparing.</summary>
        public void Pause()
        {
            m_PauseRequested = true;
            if (m_IsVideo && Video && Video.isPrepared) Video.Pause();
        }

        public void Resume()
        {
            m_PauseRequested = false;
            if (m_IsVideo && Video && Video.isPrepared) Video.Play();
        }

        public void TogglePause()
        {
            if (IsPlaying || (m_IsVideo && Video && !Video.isPrepared && !m_PauseRequested)) Pause();
            else Resume();
        }

        /// <summary>Seeks to absolute seconds, clamped to the duration. Queued until preparation completes.</summary>
        public void Seek(double seconds)
        {
            if (!m_IsVideo || double.IsNaN(seconds) || double.IsInfinity(seconds)) return;
            if (!Video.isPrepared) { m_PendingSeek = Math.Max(0, seconds); return; }
            if (Video.canSetTime) Video.time = Math.Max(0, Math.Min(seconds, Duration));
        }

        public void SetVolume(float volume)
        {
            if (!float.IsNaN(volume)) audioSource.volume = Mathf.Clamp01(volume);
        }

        static string Resolve(string src)
        {
            if (src.Contains("://") || Path.IsPathRooted(src)) return src;
            return Path.Combine(Application.streamingAssetsPath, src);
        }

        IEnumerator LoadImage(string url)
        {
            Status = $"loading image {source}";
            if (!url.Contains("://")) url = "file://" + url;
            using var req = UnityWebRequestTexture.GetTexture(url);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) { Status = $"<color=#f88>image error: {req.error}</color>"; yield break; }
            if (m_Image) Destroy(m_Image);
            m_Image = DownloadHandlerTexture.GetContent(req);
            Show(m_Image);
            Status = $"image {m_Image.width}x{m_Image.height} {layout}";
        }

        void PlayVideo(string url)
        {
            m_IsVideo = true;
            if (!Video)
            {
                Video = gameObject.AddComponent<VideoPlayer>();
                Video.playOnAwake = false;
                Video.renderMode = VideoRenderMode.APIOnly;
                Video.audioOutputMode = VideoAudioOutputMode.AudioSource;
                Video.prepareCompleted += OnPrepared;
                Video.errorReceived += OnVideoError;
            }
            Video.source = VideoSource.Url;
            Video.url = url;
            Video.isLooping = loop;
            Video.EnableAudioTrack(0, true);
            Video.SetTargetAudioSource(0, audioSource);
            Status = $"preparing video {source}";
            Video.Prepare();
        }

        void OnPrepared(VideoPlayer vp)
        {
            if (!m_IsVideo) return;
            Show(vp.texture);
            if (m_PendingSeek.HasValue) { Seek(m_PendingSeek.Value); m_PendingSeek = null; }
            if (!m_PauseRequested) vp.Play();
            Status = $"video {layout}";
        }

        void OnVideoError(VideoPlayer vp, string message) => Status = $"<color=#f88>video error: {message}</color>";

        void Show(Texture tex)
        {
            if (!tex) return;
            m_Mat.mainTexture = tex;
            m_Mat.SetFloat(s_Layout, (float)layout);
            m_Mat.SetFloat(s_Swap, swapEyes ? 1f : 0f);
            if (projection != MediaProjection.Flat) return;
            float w = tex.width, h = tex.height;
            if (layout == StereoLayout.SideBySide) w *= 0.5f;
            if (layout == StereoLayout.TopBottom) h *= 0.5f;
            transform.localScale = new Vector3(widthMeters, widthMeters * h / Mathf.Max(1f, w), 1f);
        }

        void SetProjection()
        {
            m_Mat.SetFloat(s_Projection, (float)projection);
            m_Mat.SetFloat(s_Layout, (float)layout);
            m_Mat.SetFloat(s_Swap, swapEyes ? 1f : 0f);
            if (projection == MediaProjection.Flat)
            {
                if (!m_PanoramaMesh) return;
                m_Filter.sharedMesh = m_FlatMesh;
                transform.localPosition = m_FlatPosition;
                transform.localRotation = m_FlatRotation;
                transform.localScale = m_FlatScale;
                Destroy(m_PanoramaMesh);
                m_PanoramaMesh = null;
                return;
            }

            if (!m_PanoramaMesh)
            {
                m_FlatPosition = transform.localPosition;
                m_FlatRotation = transform.localRotation;
                m_FlatScale = transform.localScale;
            }
            else Destroy(m_PanoramaMesh);
            m_PanoramaMesh = PanoramaMesh.Create(projection, Mathf.Max(0.1f, radiusMeters));
            m_Filter.sharedMesh = m_PanoramaMesh;

            var head = Camera.main;
            if (head)
            {
                var forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up);
                if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
                transform.SetPositionAndRotation(head.transform.position, Quaternion.LookRotation(forward, Vector3.up));
            }
            // Content screens can have scaled parents. Keep the panorama radius in world metres.
            var scale = transform.parent ? transform.parent.lossyScale : Vector3.one;
            transform.localScale = new Vector3(InverseScale(scale.x), InverseScale(scale.y), InverseScale(scale.z));
        }

        static float InverseScale(float value) => Mathf.Abs(value) > 1e-5f ? 1f / value : 1f;

        void OnDestroy()
        {
            if (Video)
            {
                Video.prepareCompleted -= OnPrepared;
                Video.errorReceived -= OnVideoError;
                Video.Stop();
            }
            if (m_PanoramaMesh) Destroy(m_PanoramaMesh);
            if (m_Image) Destroy(m_Image);
            if (m_Mat) Destroy(m_Mat);
        }
    }
}
