using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace ImmerseTheBay.Media
{
    public enum StereoLayout { Mono = 0, SideBySide = 1, TopBottom = 2 }

    /// <summary>
    /// Shows an image or video on this object's quad, in mono or stereo (side-by-side / top-bottom).
    /// Source can be an http(s) URL, an absolute file path, or a path relative to StreamingAssets.
    /// The renderer must use a material with the ImmerseTheBay/StereoUnlit shader.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer))]
    public class SpatialMediaPlayer : MonoBehaviour
    {
        [Tooltip("URL, absolute path, or path relative to StreamingAssets. Leave empty to load later via Play().")]
        public string source;
        public StereoLayout layout = StereoLayout.SideBySide;
        public bool swapEyes;
        [Tooltip("Width of the screen in metres; height follows the media aspect ratio.")]
        public float widthMeters = 1.2f;
        public bool loop = true;
        public bool playOnStart = true;
        [Tooltip("Video audio is routed here so the waveform visualiser can see it.")]
        public AudioSource audioSource;

        public string Status { get; private set; } = "idle";
        public VideoPlayer Video { get; private set; }

        static readonly int s_Layout = Shader.PropertyToID("_Layout");
        static readonly int s_Swap = Shader.PropertyToID("_SwapEyes");
        Material m_Mat;
        Texture2D m_Image;
        Func<string> m_Debug;

        void Awake()
        {
            m_Mat = GetComponent<MeshRenderer>().material;
            if (!audioSource) audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            m_Debug = () => $"<b>== MEDIA ==</b>\n{Status}" +
                (Video && Video.isPrepared ? $"\nvideo {Video.width}x{Video.height} t={Video.time:F1}/{Video.length:F1}s playing={Video.isPlaying}" : "");
        }

        void OnEnable() => DebugRegistry.Register(m_Debug);
        void OnDisable() => DebugRegistry.Unregister(m_Debug);

        void Start()
        {
            if (playOnStart && !string.IsNullOrEmpty(source)) Play(source, layout);
            else if (string.IsNullOrEmpty(source)) Status = "no source set";
        }

        public void Play(string src, StereoLayout stereo)
        {
            source = src; layout = stereo;
            StopAllCoroutines();
            if (Video) Video.Stop();
            string url = Resolve(src);
            string ext = Path.GetExtension(src.Split('?')[0]).ToLowerInvariant();
            bool isImage = ext is ".png" or ".jpg" or ".jpeg";
            if (isImage) StartCoroutine(LoadImage(url));
            else PlayVideo(url);
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
            if (!Video)
            {
                Video = gameObject.AddComponent<VideoPlayer>();
                Video.playOnAwake = false;
                Video.renderMode = VideoRenderMode.APIOnly;
                Video.audioOutputMode = VideoAudioOutputMode.AudioSource;
                Video.prepareCompleted += OnPrepared;
                Video.errorReceived += (_, msg) => Status = $"<color=#f88>video error: {msg}</color>";
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
            Show(vp.texture);
            vp.Play();
            Status = $"video {layout}";
        }

        void Show(Texture tex)
        {
            m_Mat.mainTexture = tex;
            m_Mat.SetFloat(s_Layout, (float)layout);
            m_Mat.SetFloat(s_Swap, swapEyes ? 1f : 0f);
            float w = tex.width, h = tex.height;
            if (layout == StereoLayout.SideBySide) w *= 0.5f;
            if (layout == StereoLayout.TopBottom) h *= 0.5f;
            transform.localScale = new Vector3(widthMeters, widthMeters * h / Mathf.Max(1f, w), 1f);
        }

        void OnDestroy()
        {
            if (m_Image) Destroy(m_Image);
            if (m_Mat) Destroy(m_Mat);
        }
    }
}
