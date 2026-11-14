using System;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace ImmerseTheBay.Visko
{
    /// <summary>
    /// Captures the phone's rear camera, not the Air 2 Ultra tracking cameras.
    /// Call on Unity's main thread. The returned JPEG is upright, center-cropped to 16:9.
    /// </summary>
    public class PhotoCapture : MonoBehaviour
    {
        const int OutputWidth = 1280, OutputHeight = 720;
        [Range(30, 100)] public int jpegQuality = 85;
        [Min(1)] public float frameTimeoutSeconds = 12;
        public bool IsCapturing { get; private set; }
        public string Status { get; private set; } = "idle";

        // Android supports only one WebCamTexture at a time, including across component instances.
        static PhotoCapture s_CameraOwner;
        WebCamTexture m_Camera;
        int m_Generation;
        Func<string> m_Debug;

        void Awake() => m_Debug = () => $"<b>== PHONE PHOTO ==</b>\n{Status}";
        void OnEnable() => DebugRegistry.Register(m_Debug);
        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            CancelCapture();
        }
        void OnApplicationPause(bool paused) { if (paused) CancelCapture(); }

        /// <summary>Cancel an in-flight camera request and release the camera immediately.</summary>
        public void CancelCapture()
        {
            if (!IsCapturing) return;
            m_Generation++;
            ReleaseCamera();
            IsCapturing = false;
            Status = "capture cancelled";
        }

        /// <summary>Capture one 1280x720 JPEG. Fails if no rear camera exists or another capture is active.</summary>
        public async Task<byte[]> CaptureJpeg()
        {
            if (!isActiveAndEnabled) throw new InvalidOperationException("Photo capture is disabled.");
            if (IsCapturing || s_CameraOwner) throw new InvalidOperationException("A phone camera capture is already active.");
            s_CameraOwner = this;
            IsCapturing = true;
            int generation = ++m_Generation;
            Texture2D photo = null;
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
                {
                    Status = "requesting camera permission";
                    Permission.RequestUserPermission(Permission.Camera);
                    float permissionDeadline = Time.realtimeSinceStartup + 20;
                    while (!Permission.HasUserAuthorizedPermission(Permission.Camera))
                    {
                        CheckCapture(generation);
                        if (Time.realtimeSinceStartup >= permissionDeadline)
                            throw new InvalidOperationException("Camera permission was denied or the permission request timed out.");
                        await Task.Yield();
                    }
                }
#else
                if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
                {
                    Status = "requesting camera permission";
                    var permission = Application.RequestUserAuthorization(UserAuthorization.WebCam);
                    float permissionDeadline = Time.realtimeSinceStartup + 20;
                    while (!permission.isDone)
                    {
                        CheckCapture(generation);
                        if (Time.realtimeSinceStartup >= permissionDeadline) throw new TimeoutException("Camera permission request timed out.");
                        await Task.Yield();
                    }
                    if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
                        throw new InvalidOperationException("Camera permission was denied.");
                }
#endif
                CheckCapture(generation);
                string deviceName = null;
                foreach (var device in WebCamTexture.devices)
                    if (!device.isFrontFacing) { deviceName = device.name; break; }
                if (deviceName == null) throw new InvalidOperationException("No rear phone camera is available.");

                Status = "opening rear camera";
                m_Camera = new WebCamTexture(deviceName, OutputWidth, OutputHeight, 30);
                m_Camera.Play();
                float deadline = Time.realtimeSinceStartup + Mathf.Max(1, frameTimeoutSeconds);
                int lastFrame = -1, warmFrames = 0;
                while (warmFrames < 2)
                {
                    CheckCapture(generation);
                    if (Time.realtimeSinceStartup >= deadline) throw new TimeoutException("No usable rear-camera frame arrived.");
                    if (m_Camera.didUpdateThisFrame && m_Camera.width > 16 && m_Camera.height > 16 && lastFrame != Time.frameCount)
                    {
                        lastFrame = Time.frameCount;
                        warmFrames++;
                    }
                    if (warmFrames < 2) await Task.Yield();
                }
                CheckCapture(generation);
                // Device resolution and orientation can differ from the requested values.
                int width = m_Camera.width, height = m_Camera.height;
                var pixels = NormalizeAndCrop(m_Camera.GetPixels32(), width, height,
                    m_Camera.videoRotationAngle, m_Camera.videoVerticallyMirrored, OutputWidth, OutputHeight);
                ReleaseCamera();
                photo = new Texture2D(OutputWidth, OutputHeight, TextureFormat.RGB24, false);
                photo.SetPixels32(pixels);
                photo.Apply(false, false);
                var jpeg = photo.EncodeToJPG(Mathf.Clamp(jpegQuality, 30, 100));
                if (jpeg == null || jpeg.Length == 0) throw new InvalidOperationException("The camera frame could not be encoded as JPEG.");
                Status = $"captured {OutputWidth}x{OutputHeight} JPEG ({jpeg.Length / 1024} KiB)";
                return jpeg;
            }
            catch (Exception e)
            {
                if (generation == m_Generation) Status = "capture failed: " + e.Message;
                throw;
            }
            finally
            {
                if (photo) Destroy(photo);
                // An old cancelled continuation must not release a newer capture's camera.
                if (generation == m_Generation)
                {
                    ReleaseCamera();
                    IsCapturing = false;
                }
            }
        }

        /// <summary>Capture and wait until Reactor accepts the uploaded image before returning.</summary>
        public async Task CaptureAndSetImage(ViskoSession session)
        {
            if (!session) throw new ArgumentNullException(nameof(session));
            byte[] jpeg = await CaptureJpeg();
            await session.SetImage(jpeg);
        }

        void CheckCapture(int generation)
        {
            if (!this || !isActiveAndEnabled || generation != m_Generation)
                throw new OperationCanceledException("Phone photo capture was cancelled.");
        }

        void ReleaseCamera()
        {
            if (m_Camera)
            {
                m_Camera.Stop();
                Destroy(m_Camera);
                m_Camera = null;
            }
            if (s_CameraOwner == this) s_CameraOwner = null;
        }

        // Pixel coordinates here are Unity's bottom-left origin. Undo the source vertical mirror,
        // then apply clockwise camera rotation. Crop in upright coordinates before resampling.
        internal static Color32[] NormalizeAndCrop(Color32[] source, int width, int height,
            int rotation, bool verticallyMirrored, int outputWidth, int outputHeight)
        {
            if (source == null || width <= 0 || height <= 0 || source.Length != width * height)
                throw new ArgumentException("Invalid camera pixels.");
            if (outputWidth <= 0 || outputHeight <= 0) throw new ArgumentException("Invalid output dimensions.");
            rotation = ((rotation % 360) + 360) % 360;
            if (rotation % 90 != 0) throw new ArgumentException("Camera rotation must be a multiple of 90 degrees.");
            int uprightWidth = rotation == 90 || rotation == 270 ? height : width;
            int uprightHeight = rotation == 90 || rotation == 270 ? width : height;
            var upright = new Color32[source.Length];
            for (int y = 0; y < uprightHeight; y++)
                for (int x = 0; x < uprightWidth; x++)
                {
                    int sx, sy;
                    switch (rotation)
                    {
                        case 90: sx = width - 1 - y; sy = x; break;
                        case 180: sx = width - 1 - x; sy = height - 1 - y; break;
                        case 270: sx = y; sy = height - 1 - x; break;
                        default: sx = x; sy = y; break;
                    }
                    if (verticallyMirrored) sy = height - 1 - sy;
                    upright[y * uprightWidth + x] = source[sy * width + sx];
                }

            double aspect = (double)outputWidth / outputHeight;
            double cropWidth = Math.Min(uprightWidth, uprightHeight * aspect);
            double cropHeight = cropWidth / aspect;
            double offsetX = (uprightWidth - cropWidth) * 0.5;
            double offsetY = (uprightHeight - cropHeight) * 0.5;
            var result = new Color32[outputWidth * outputHeight];
            for (int y = 0; y < outputHeight; y++)
                for (int x = 0; x < outputWidth; x++)
                {
                    double px = Math.Max(0, Math.Min(uprightWidth - 1, offsetX + (x + 0.5) * cropWidth / outputWidth - 0.5));
                    double py = Math.Max(0, Math.Min(uprightHeight - 1, offsetY + (y + 0.5) * cropHeight / outputHeight - 0.5));
                    int x0 = (int)px, y0 = (int)py;
                    int x1 = Math.Min(x0 + 1, uprightWidth - 1), y1 = Math.Min(y0 + 1, uprightHeight - 1);
                    var a = upright[y0 * uprightWidth + x0];
                    var b = upright[y0 * uprightWidth + x1];
                    var c = upright[y1 * uprightWidth + x0];
                    var d = upright[y1 * uprightWidth + x1];
                    double fx = px - x0, fy = py - y0;
                    result[y * outputWidth + x] = new Color32(
                        Blend(a.r, b.r, c.r, d.r, fx, fy), Blend(a.g, b.g, c.g, d.g, fx, fy),
                        Blend(a.b, b.b, c.b, d.b, fx, fy), 255);
                }
            return result;
        }

        static byte Blend(byte a, byte b, byte c, byte d, double x, double y) =>
            (byte)Math.Round((a + (b - a) * x) * (1 - y) + (c + (d - c) * x) * y);
    }
}
