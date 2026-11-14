using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace ImmerseTheBay.Content
{
    /// <summary>Marks a spawned object (2D video / image / 3D model) as grabbable, scalable and anchor-persistable.</summary>
    public class PlaceableContent : MonoBehaviour
    {
        public ContentItem item;
        public ARAnchor anchor;
        public bool locked;

        Renderer[] m_Renderers;
        GameObject m_Outline;

        public void RefreshRenderers() => m_Renderers = GetComponentsInChildren<Renderer>();

        /// <summary>World-space bounds of all child renderers.</summary>
        public Bounds GetBounds()
        {
            if (m_Renderers == null || m_Renderers.Length == 0) RefreshRenderers();
            var b = new Bounds(transform.position, Vector3.one * 0.05f);
            bool first = true;
            foreach (var r in m_Renderers)
            {
                if (!r || r.gameObject == m_Outline) continue;
                if (first) { b = r.bounds; first = false; }
                else b.Encapsulate(r.bounds);
            }
            return b;
        }

        public void SetHighlight(bool on)
        {
            if (on && !m_Outline)
            {
                m_Outline = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(m_Outline.GetComponent<Collider>());
                m_Outline.name = "Highlight";
                var r = m_Outline.GetComponent<Renderer>();
                var mat = r.material;
                var c = new Color(0.3f, 0.9f, 1f, 1f);
                mat.color = c;
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            }
            if (!m_Outline) return;
            m_Outline.SetActive(on);
            if (on)
            {
                var b = GetBounds();
                // thin marker under the object's bounds
                m_Outline.transform.SetPositionAndRotation(new Vector3(b.center.x, b.min.y - 0.01f, b.center.z), Quaternion.identity);
                m_Outline.transform.localScale = new Vector3(b.size.x, 0.004f, b.size.z);
            }
        }

        void OnDestroy() { if (m_Outline) Destroy(m_Outline); }
    }
}
