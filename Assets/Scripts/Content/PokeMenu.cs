using System;
using System.Collections.Generic;
using ImmerseTheBay.Tracking;
using UnityEngine;
using UnityEngine.UI;
using Keyboard = UnityEngine.InputSystem.Keyboard;

namespace ImmerseTheBay.Content
{
    /// <summary>
    /// Floating world-space button panel you press with your index fingertip (poke).
    /// One button per item in content.json (spawns it in front of you) plus system actions:
    /// Save layout / Clear / Delete last / Recenter / HUD on-off.
    /// Keyboard fallback in the editor: 1-9 spawn items, F5 save, Del delete last, R recenter.
    /// </summary>
    public class PokeMenu : MonoBehaviour
    {
        public ContentSpawner spawner;
        public RecenterController recenter;
        public GameObject hud;
        public ImmerseTheBay.Xreal.XrealFeatures xreal;
        public ImmerseTheBay.Visko.ViskoSession visko;
        public ImmerseTheBay.Cast.FirstPersonCaster caster;
        public ImmerseTheBay.Learning.LearningAgentClient learning;
        public ImmerseTheBay.Visko.PhotoCapture photoCapture;

        [Header("Placement")]
        public float distance = 0.45f;
        public float heightOffset = -0.25f;
        [Tooltip("Menu re-centres in front of you when you turn further than this (degrees).")]
        public float followAngle = 45f;

        [Header("Poke")]
        public float pressDepth = 0.015f;
        public float cooldown = 0.6f;

        const float PixelSize = 0.0006f;

        class Button
        {
            public RectTransform rect;
            public Image image;
            public Action action;
            public bool inside;
        }

        readonly List<Button> m_Buttons = new();
        bool m_ViskoPaused, m_Casting;
        RectTransform m_Panel;
        Font m_Font;
        float m_LastPress;
        Transform m_Cam;

        async void Start()
        {
            if (!spawner) spawner = FindAnyObjectByType<ContentSpawner>();
            if (!recenter) recenter = FindAnyObjectByType<RecenterController>();
            if (!xreal) xreal = FindAnyObjectByType<ImmerseTheBay.Xreal.XrealFeatures>();
            if (!visko) visko = FindAnyObjectByType<ImmerseTheBay.Visko.ViskoSession>();
            if (!caster) caster = FindAnyObjectByType<ImmerseTheBay.Cast.FirstPersonCaster>();
            m_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildCanvas();
            var manifest = await ContentLibrary.LoadAsync();
            foreach (var item in manifest.items)
            {
                var captured = item;
                AddButton(item.label, new Color(0.15f, 0.35f, 0.6f, 0.9f), () => spawner.SpawnInFront(captured));
            }
            AddButton("Save layout", new Color(0.15f, 0.5f, 0.25f, 0.9f), () => spawner.SaveLayout());
            AddButton("Delete last", new Color(0.55f, 0.35f, 0.1f, 0.9f), () => spawner.DeleteLast());
            AddButton("Clear all", new Color(0.6f, 0.15f, 0.15f, 0.9f), () => spawner.ClearAll());
            AddButton("Recenter", new Color(0.35f, 0.25f, 0.55f, 0.9f), () => recenter?.Recenter());
            AddButton("Hands / Controller", new Color(0.2f, 0.4f, 0.45f, 0.9f), () => xreal?.SwitchInputSource());
            AddButton("6DoF / 3DoF / 0DoF", new Color(0.2f, 0.4f, 0.45f, 0.9f), () => xreal?.CycleTrackingType());
            var viskoColor = new Color(0.45f, 0.2f, 0.5f, 0.9f);
            AddButton("Lesson: begin", viskoColor, () => learning?.BeginLesson());
            AddButton("Lesson: phone photo", viskoColor, BeginPhotoLesson);
            AddButton("Lesson: point", viskoColor, () => learning?.context?.Point());
            AddButton("Lesson: leave", viskoColor, () => learning?.LeaveLesson());
            AddButton("Visko: new session", viskoColor, () => visko?.CreateSession());
            AddButton("Visko: join portal", viskoColor, () => visko?.JoinPortalSession());
            AddButton("Visko: start", viskoColor, () => visko?.StartGeneration());
            AddButton("Visko: pause/resume", viskoColor, () => { if (visko == null) return; if (m_ViskoPaused) visko.Resume(); else visko.Pause(); m_ViskoPaused = !m_ViskoPaused; });
            AddButton("Visko: leave", viskoColor, () => visko?.Leave());
            AddButton("Cast view on/off", new Color(0.2f, 0.45f, 0.3f, 0.9f), () => { if (caster) { m_Casting = !m_Casting; caster.SetCasting(m_Casting); } });
            AddButton("HUD on/off", new Color(0.3f, 0.3f, 0.3f, 0.9f), () => { if (hud) hud.SetActive(!hud.activeSelf); });
            Layout();
            PlaceInFront();
        }

        async void BeginPhotoLesson()
        {
            if (!learning || !photoCapture || photoCapture.IsCapturing) return;
            try
            {
                var image = await photoCapture.CaptureJpeg();
                if (this && learning && isActiveAndEnabled) learning.BeginLessonWithImage(image);
            }
            catch (Exception error) { Debug.LogWarning("Phone photo lesson: " + error.Message); }
        }

        void BuildCanvas()
        {
            var go = new GameObject("MenuCanvas", typeof(Canvas));
            go.transform.SetParent(transform, false);
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            m_Panel = (RectTransform)go.transform;
            m_Panel.localScale = Vector3.one * PixelSize;
            var bg = go.AddComponent<Image>();
            bg.color = new Color(0, 0, 0, 0.35f);
        }

        void AddButton(string label, Color color, Action action)
        {
            var go = new GameObject($"Btn_{label}", typeof(Image));
            go.transform.SetParent(m_Panel, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            var textGo = new GameObject("Label", typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            var trt = (RectTransform)textGo.transform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = trt.offsetMax = Vector2.zero;
            var t = textGo.GetComponent<Text>();
            t.font = m_Font; t.fontSize = 26; t.alignment = TextAnchor.MiddleCenter; t.color = Color.white; t.text = label;
            m_Buttons.Add(new Button { rect = (RectTransform)go.transform, image = img, action = action });
        }

        void Layout()
        {
            const int cols = 4; const float w = 200, h = 80, gap = 14;
            int rows = Mathf.CeilToInt(m_Buttons.Count / (float)cols);
            m_Panel.sizeDelta = new Vector2(cols * (w + gap) + gap, rows * (h + gap) + gap);
            for (int i = 0; i < m_Buttons.Count; i++)
            {
                var rt = m_Buttons[i].rect;
                rt.sizeDelta = new Vector2(w, h);
                int c = i % cols, r = i / cols;
                rt.anchoredPosition = new Vector2(-m_Panel.sizeDelta.x / 2 + gap + w / 2 + c * (w + gap),
                                                   m_Panel.sizeDelta.y / 2 - gap - h / 2 - r * (h + gap));
            }
        }

        void PlaceInFront()
        {
            if (!m_Cam) return;
            var fwd = Vector3.ProjectOnPlane(m_Cam.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 1e-4f) return;
            transform.position = m_Cam.position + fwd * distance + Vector3.up * heightOffset;
            transform.rotation = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(25, 0, 0); // tilted like a desk panel
        }

        void Update()
        {
            if (!m_Cam && Camera.main) { m_Cam = Camera.main.transform; PlaceInFront(); }
            if (!m_Cam || m_Panel == null) return;

            var fwd = Vector3.ProjectOnPlane(m_Cam.forward, Vector3.up);
            var toMenu = Vector3.ProjectOnPlane(transform.position - m_Cam.position, Vector3.up);
            if (Vector3.Angle(fwd, toMenu) > followAngle || toMenu.magnitude > distance * 2.5f) PlaceInFront();

            var hands = HandInput.Instance;
            if (hands)
            {
                Poke(hands.Left);
                Poke(hands.Right);
            }
            KeyboardFallback();
        }

        void Poke(HandState h)
        {
            if (!h.tracked) return;
            foreach (var b in m_Buttons)
            {
                var local = b.rect.InverseTransformPoint(h.indexTip);
                // Canvas +Z points away from the user, so positive depth = fingertip pushed through the panel.
                float depthMetres = local.z * PixelSize;
                bool overRect = b.rect.rect.Contains(new Vector2(local.x, local.y));
                bool inside = overRect && depthMetres > pressDepth - 0.02f && depthMetres < 0.05f;
                b.image.transform.localScale = overRect && depthMetres > -0.03f && depthMetres < 0.05f ? Vector3.one * 1.06f : Vector3.one;
                if (inside && !b.inside && Time.time - m_LastPress > cooldown)
                {
                    m_LastPress = Time.time;
                    b.action?.Invoke();
                }
                b.inside = inside;
            }
        }

        void KeyboardFallback()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            var items = ContentLibrary.Current?.items;
            for (int i = 0; items != null && i < Mathf.Min(9, items.Count); i++)
                if (kb[UnityEngine.InputSystem.Key.Digit1 + i].wasPressedThisFrame) spawner.SpawnInFront(items[i]);
            if (kb.f5Key.wasPressedThisFrame) spawner.SaveLayout();
            if (kb.deleteKey.wasPressedThisFrame) spawner.DeleteLast();
            if (kb.rKey.wasPressedThisFrame) recenter?.Recenter();
        }
    }
}
