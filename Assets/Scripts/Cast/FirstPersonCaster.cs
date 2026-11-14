using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ImmerseTheBay.Cast
{
    /// <summary>Opt-in virtual-only left-eye JPEG cast. Add once to the generated scene.</summary>
    public sealed class FirstPersonCaster : MonoBehaviour
    {
        [Serializable]
        sealed class CastConfig
        {
            public string portalUrl = "";
            public string portalToken = "";
        }

        public Camera sourceCamera;
        [Tooltip("Casting is off by default. V or SetCasting(bool) changes it at runtime.")]
        public bool castOnStart;
        [Range(1, 100)] public int jpegQuality = 70;
        [Tooltip("Reverse GPU rows before encoding. Unity/OpenGL render textures normally start at the bottom.")]
        public bool flipVertically = true;

        public bool IsCasting { get; private set; }
        const int Width = 640, Height = 360;
        const float Interval = 0.1f;
        readonly ConcurrentQueue<Action> m_Main = new();
        readonly CastConfig m_Config = new();
        Camera m_Camera;
        RenderTexture m_Target;
        CastSocket m_Socket;
        Coroutine m_ConfigRoutine;
        AsyncGPUReadbackRequest m_Readback;
        Func<string> m_Debug;
        bool m_ConfigLoaded, m_Busy, m_ReadbackPending, m_Paused;
        int m_Generation, m_Frames;
        long m_Bytes;
        float m_NextFrame, m_StatsTime, m_Fps, m_BytesPerSecond;
        string m_Status = "off", m_ConfigError = "";

        void Awake()
        {
            IsCasting = castOnStart;
            m_Debug = () => $"<b>== FIRST PERSON CAST ==</b>\n" +
                $"{(IsCasting ? "on" : "off")} {m_Status}  {m_Fps:F1} fps  {m_BytesPerSecond:F0} B/s\n" +
                $"virtual left eye {Width}x{Height} / 10 fps max (V toggles)";
        }

        void OnEnable()
        {
            DebugRegistry.Register(m_Debug);
            if (!m_ConfigLoaded) m_ConfigRoutine = StartCoroutine(LoadConfig());
        }

        IEnumerator LoadConfig()
        {
            var path = Application.streamingAssetsPath.TrimEnd('/') + "/visko.json";
            if (!path.Contains("://")) path = new Uri(path).AbsoluteUri;
            using (var request = UnityWebRequest.Get(path))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    try { JsonUtility.FromJsonOverwrite(request.downloadHandler.text, m_Config); }
                    catch (Exception) { m_ConfigError = "invalid visko.json"; }
                }
                else m_ConfigError = "visko.json unavailable";
            }
            m_ConfigLoaded = true;
            m_ConfigRoutine = null;
            if (m_ConfigError != "") m_Status = m_ConfigError;
        }

        /// <summary>Main-thread API. Enabling waits for visko.json; disabling aborts the socket immediately.</summary>
        public void SetCasting(bool enabled)
        {
            IsCasting = enabled;
            if (!enabled) StopCasting();
        }

        void OnApplicationPause(bool paused)
        {
            m_Paused = paused;
            if (paused) StopCasting();
        }

        void Update()
        {
            while (m_Main.TryDequeue(out var action)) action();
            if (Keyboard.current?.vKey.wasPressedThisFrame == true) SetCasting(!IsCasting);
            m_StatsTime += Time.unscaledDeltaTime;
            if (m_StatsTime >= 1f)
            {
                m_Fps = m_Frames / m_StatsTime;
                m_BytesPerSecond = m_Bytes / m_StatsTime;
                m_Frames = 0;
                m_Bytes = 0;
                m_StatsTime = 0;
            }
            if (!IsCasting || m_Paused || !m_ConfigLoaded) return;
            if (m_ConfigError != "") { m_Status = m_ConfigError; return; }
            if (!SystemInfo.supportsAsyncGPUReadback) { m_Status = "async GPU readback unsupported"; return; }
            if (m_Socket == null) OpenSocket();
        }

        void LateUpdate()
        {
            if (!IsCasting || m_Paused || m_Socket == null || !m_Socket.IsConnected || m_Busy ||
                Time.realtimeSinceStartup < m_NextFrame) return;
            var source = sourceCamera ? sourceCamera : Camera.main;
            if (!source) { m_Status = "waiting for camera"; return; }
            m_NextFrame = Time.realtimeSinceStartup + Interval;
            try
            {
                EnsureTarget();
                m_Camera.CopyFrom(source);
                m_Camera.enabled = false;
                m_Camera.stereoTargetEye = StereoTargetEyeMask.None;
                m_Camera.targetTexture = m_Target;
                m_Camera.rect = new Rect(0, 0, 1, 1);
                m_Camera.allowHDR = false;
                m_Camera.allowMSAA = false;
                m_Camera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                m_Camera.worldToCameraMatrix = source.stereoEnabled
                    ? source.GetStereoViewMatrix(Camera.StereoscopicEye.Left) : source.worldToCameraMatrix;
                m_Camera.projectionMatrix = source.stereoEnabled
                    ? source.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) : source.projectionMatrix;
                if (GraphicsSettings.currentRenderPipeline != null)
                {
                    var render = new UniversalRenderPipeline.SingleCameraRequest { destination = m_Target };
                    if (!RenderPipeline.SupportsRenderRequest(m_Camera, render))
                        throw new NotSupportedException("render request");
                    RenderPipeline.SubmitRenderRequest(m_Camera, render);
                }
                else m_Camera.Render();

                m_Busy = true;
                m_ReadbackPending = true;
                int generation = m_Generation;
                var socket = m_Socket;
                int quality = Mathf.Clamp(jpegQuality, 1, 100);
                bool flip = flipVertically;
                // Copy request-owned NativeArray immediately; it is invalid after this callback.
                m_Readback = AsyncGPUReadback.Request(m_Target, 0, TextureFormat.RGBA32, request =>
                {
                    byte[] pixels = null;
                    try
                    {
                        if (!request.hasError) pixels = request.GetData<byte>().ToArray();
                    }
                    catch (Exception) { /* Report failed readback through Update. */ }
                    finally
                    {
                        var copy = pixels;
                        m_Main.Enqueue(() => ReadbackReady(copy, generation, socket, quality, flip));
                    }
                });
            }
            catch (Exception e)
            {
                m_Busy = m_ReadbackPending = false;
                StopCasting();
                IsCasting = false;
                m_Status = $"capture failed ({e.GetType().Name}); V retries";
            }
        }

        void EnsureTarget()
        {
            if (!m_Target)
            {
                m_Target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { name = "FirstPersonCast", antiAliasing = 1 };
                if (!m_Target.Create()) throw new InvalidOperationException("render texture");
            }
            if (m_Camera) return;
            var go = new GameObject("FirstPersonCastCamera");
            go.transform.SetParent(transform, false);
            m_Camera = go.AddComponent<Camera>();
            m_Camera.enabled = false;
            // URP must render this camera in mono, even while the main camera is XR-enabled.
            var data = go.AddComponent<UniversalAdditionalCameraData>();
            data.allowXRRendering = false;
            data.renderPostProcessing = false;
        }

        void ReadbackReady(byte[] pixels, int generation, CastSocket socket, int quality, bool flip)
        {
            m_ReadbackPending = false;
            if (generation != m_Generation || !IsCasting || !isActiveAndEnabled)
            {
                m_Busy = false;
                ReleaseTarget();
                return;
            }
            if (pixels == null)
            {
                m_Busy = false;
                SetCasting(false);
                m_Status = "GPU readback failed; V retries";
                return;
            }
            _ = Task.Run(async () =>
            {
                int bytes = 0;
                string error = null;
                try
                {
                    if (flip) FlipRows(pixels);
                    var jpeg = ImageConversion.EncodeArrayToJPG(pixels, GraphicsFormat.R8G8B8A8_UNorm,
                        Width, Height, 0, quality);
                    if (jpeg == null || jpeg.Length == 0) throw new InvalidOperationException("JPEG encoder");
                    if (await socket.SendFrameAsync(jpeg).ConfigureAwait(false)) bytes = jpeg.Length;
                }
                catch (Exception e) { error = e.GetType().Name; }
                m_Main.Enqueue(() =>
                {
                    m_Busy = false;
                    if (generation != m_Generation) return;
                    if (error != null) m_Status = $"encode failed ({error})";
                    if (bytes > 0) { m_Status = "streaming"; m_Frames++; m_Bytes += bytes; }
                });
            });
        }

        static void FlipRows(byte[] pixels)
        {
            const int stride = Width * 4;
            var row = new byte[stride];
            for (int y = 0; y < Height / 2; y++)
            {
                int top = y * stride, bottom = (Height - 1 - y) * stride;
                Buffer.BlockCopy(pixels, top, row, 0, stride);
                Buffer.BlockCopy(pixels, bottom, pixels, top, stride);
                Buffer.BlockCopy(row, 0, pixels, bottom, stride);
            }
        }

        void OpenSocket()
        {
            if (!Uri.TryCreate(m_Config.portalUrl, UriKind.Absolute, out var portal) ||
                (portal.Scheme != "http" && portal.Scheme != "https"))
            {
                m_Status = "visko.json portalUrl must be http(s)";
                return;
            }
            var uri = new UriBuilder(portal)
            {
                Scheme = portal.Scheme == "https" ? "wss" : "ws",
                Path = portal.AbsolutePath.TrimEnd('/') + "/cast",
                Query = "role=publisher" + (string.IsNullOrEmpty(m_Config.portalToken) ? "" :
                    "&portal_token=" + Uri.EscapeDataString(m_Config.portalToken)),
                Fragment = ""
            };
            int generation = m_Generation;
            m_Socket = new CastSocket(uri.Uri, status => m_Main.Enqueue(() =>
            {
                if (generation == m_Generation) m_Status = status;
            }));
        }

        void StopCasting()
        {
            m_Generation++;
            m_Socket?.Dispose();
            m_Socket = null;
            m_Status = "off";
            m_Fps = m_BytesPerSecond = 0;
            m_Frames = 0;
            m_Bytes = 0;
            // A RenderTexture must remain alive until its GPU readback has completed.
            if (!m_ReadbackPending) ReleaseTarget();
        }

        void ReleaseTarget()
        {
            if (m_Camera) Destroy(m_Camera.gameObject);
            m_Camera = null;
            if (m_Target) { m_Target.Release(); Destroy(m_Target); }
            m_Target = null;
        }

        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            if (m_ConfigRoutine != null) StopCoroutine(m_ConfigRoutine);
            m_ConfigRoutine = null;
            StopCasting();
        }

        void OnDestroy()
        {
            // The request callback can arrive after destruction. Release on the main thread there.
            if (m_ReadbackPending) m_Readback.WaitForCompletion();
            while (m_Main.TryDequeue(out var action)) action();
            ReleaseTarget();
        }
    }
}
