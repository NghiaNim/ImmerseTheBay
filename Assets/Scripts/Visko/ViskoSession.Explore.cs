using UnityEngine;

namespace ImmerseTheBay.Visko
{
    public partial class ViskoSession
    {
        public bool CanExploreLiveFrame => screen && screen.enabled && HasLiveFrame &&
            Time.realtimeSinceStartupAsDouble - m_LiveFrameAt <= 1.5 &&
            m_LiveBlend >= 0.99f && m_Texture && screen.material.mainTexture == m_Texture;

        /// <summary>Projects a world ray through the same crop and vertical flip used by the live material.</summary>
        public bool TryAimAtLiveFrame(Ray ray, out Vector2 point, out Vector3 hit)
        {
            point = default;
            hit = default;
            if (!CanExploreLiveFrame) return false;
            var bounds = screen.localBounds;
            var surface = screen.transform;
            var origin = surface.InverseTransformPoint(ray.origin);
            var direction = surface.InverseTransformVector(ray.direction);
            if (Mathf.Abs(direction.z) < 0.0001f || bounds.size.x <= 0f || bounds.size.y <= 0f) return false;
            float distance = (bounds.center.z - origin.z) / direction.z;
            if (distance <= 0f || distance > 5f) return false;
            var localHit = origin + distance * direction;
            var uv = new Vector2((localHit.x - bounds.min.x) / bounds.size.x, (localHit.y - bounds.min.y) / bounds.size.y);
            // Aim in the live center of the feathered surface; the periphery is the start photograph.
            var ellipse = new Vector2((uv.x - 0.5f) / 0.43f, (uv.y - 0.53f) / 0.49f);
            if (ellipse.sqrMagnitude >= 0.45f) return false;

            var material = screen.material;
            // The BGRA texture stores the first decoded row at v=0. Shader UV after the material
            // transform is therefore already a normalized coordinate from the decoded top edge.
            point = Vector2.Scale(uv, material.mainTextureScale) + material.mainTextureOffset;
            hit = surface.TransformPoint(localHit);
            return point.x is >= 0f and <= 1f && point.y is >= 0f and <= 1f;
        }

        /// <summary>Read one decoded frame without capturing the room, subtitles or hand model.</summary>
        public byte[] CaptureExploreFrame(out long frameId)
        {
            frameId = m_LastFrame;
            if (!CanExploreLiveFrame || frameId < 0 || m_Texture.width > 1920 || m_Texture.height > 1080) return null;
            int width = m_Texture.width, height = m_Texture.height;
            var bgra = m_Texture.GetRawTextureData<byte>();
            var rgb = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                int source = (height - 1 - y) * width * 4;
                int target = y * width * 3;
                for (int x = 0; x < width; x++, source += 4, target += 3)
                {
                    rgb[target] = bgra[source + 2];
                    rgb[target + 1] = bgra[source + 1];
                    rgb[target + 2] = bgra[source];
                }
            }

            var upright = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                upright.LoadRawTextureData(rgb);
                return ImageConversion.EncodeToJPG(upright, 82);
            }
            finally { Destroy(upright); }
        }
    }
}
