using ImmerseTheBay.Media;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

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
        ContentSpawner m_Spawner;
        XRGrabInteractable m_Grab;
        BoxCollider m_GrabCollider;
        Transform m_Screen;
        Vector3 m_ScreenScale;

        internal int AnchorRevision { get; private set; }
        internal bool IsSelected => m_Grab && m_Grab.isSelected;

        internal void ConfigureInteraction(ContentSpawner spawner)
        {
            if (locked) return;
            m_Spawner = spawner;

            // XRI ratios are relative to the scale captured in the transformer's Awake.
            float initialScale = Mathf.Clamp(transform.localScale.x, 0.05f, 20f);
            transform.localScale = Vector3.one * initialScale;
            m_GrabCollider = gameObject.AddComponent<BoxCollider>();
            RefreshInteractionBounds();
            var media = GetComponentInChildren<SpatialMediaPlayer>();
            if (media)
            {
                m_Screen = media.transform;
                m_ScreenScale = m_Screen.localScale;
            }
            var body = gameObject.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.isKinematic = true;

            var transformer = gameObject.AddComponent<XRGeneralGrabTransformer>();
            transformer.allowOneHandedScaling = false;
            transformer.allowTwoHandedScaling = true;
            transformer.clampScaling = true;
            transformer.minimumScaleRatio = 0.05f / initialScale;
            transformer.maximumScaleRatio = 20f / initialScale;

            m_Grab = gameObject.AddComponent<XRGrabInteractable>();
            // Re-register only after replacing Awake's auto-collected child colliders.
            m_Grab.enabled = false;
            m_Grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            m_Grab.trackPosition = true;
            m_Grab.trackRotation = true;
            m_Grab.trackScale = true;
            m_Grab.throwOnDetach = false;
            m_Grab.retainTransformParent = false;
            m_Grab.selectMode = InteractableSelectMode.Multiple;
            m_Grab.useDynamicAttach = true;
            m_Grab.addDefaultGrabTransformers = false;
            m_Grab.colliders.Clear();
            m_Grab.colliders.Add(m_GrabCollider);
            m_Grab.AddSingleGrabTransformer(transformer);
            m_Grab.AddMultipleGrabTransformer(transformer);
            m_Grab.selectEntered.AddListener(OnSelectEntered);
            m_Grab.selectExited.AddListener(OnSelectExited);
            m_Grab.hoverEntered.AddListener(OnHoverEntered);
            m_Grab.hoverExited.AddListener(OnHoverExited);
            m_Grab.enabled = true;
        }

        internal void RefreshInteractionBounds()
        {
            if (!m_GrabCollider) return;
            var world = GetBounds();
            var local = new Bounds(transform.InverseTransformPoint(world.center), Vector3.zero);
            // Transform every corner: transforming only the size is wrong for rotated roots.
            for (int i = 0; i < 8; i++)
            {
                var corner = world.center + Vector3.Scale(world.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                local.Encapsulate(transform.InverseTransformPoint(corner));
            }
            m_GrabCollider.center = local.center;
            // Screens have zero depth; give near/far interactors a usable volume.
            m_GrabCollider.size = Vector3.Max(local.size, Vector3.one * 0.01f);
        }

        internal void SetLegacyInteraction(bool legacy)
        {
            bool enableGrab = !legacy && !locked;
            if (m_Grab && m_Grab.enabled != enableGrab)
            {
                m_Grab.enabled = enableGrab;
                SetHighlight(false);
            }
        }

        internal void DetachForManipulation()
        {
            InvalidateAnchorRequest();
            transform.SetParent(null, true);
        }

        internal int InvalidateAnchorRequest() => ++AnchorRevision;

        void OnSelectEntered(SelectEnterEventArgs args) => DetachForManipulation();

        void OnSelectExited(SelectExitEventArgs args)
        {
            if (!locked && !IsSelected && isActiveAndEnabled && m_Spawner && m_Spawner.Spawned.Contains(this))
                m_Spawner.Reanchor(this);
        }

        void OnHoverEntered(HoverEnterEventArgs args) => SetHighlight(true);
        void OnHoverExited(HoverExitEventArgs args) => SetHighlight(m_Grab && m_Grab.isHovered);

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

        void LateUpdate()
        {
            // Both images and videos resize their quad after the media finishes loading.
            if (m_Screen && m_Screen.localScale != m_ScreenScale)
            {
                m_ScreenScale = m_Screen.localScale;
                RefreshInteractionBounds();
            }
            if (m_Outline && m_Outline.activeSelf) SetHighlight(true);
        }

        void OnDisable()
        {
            InvalidateAnchorRequest();
            SetHighlight(false);
        }

        void OnDestroy() { if (m_Outline) Destroy(m_Outline); }
    }
}
