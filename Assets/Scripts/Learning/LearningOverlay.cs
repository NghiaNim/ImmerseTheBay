using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace ImmerseTheBay.Learning
{
    /// <summary>Compact subtitles inside the shared video’s lower safe area. Buttons use the scene's XRI UI input module.</summary>
    public class LearningOverlay : MonoBehaviour
    {
        public Renderer screen;
        public Camera headCamera;
        [Tooltip("Optional override; otherwise loads the bundled Noto CJK font.")]
        public Font subtitleFont;
        public bool showKana = true;
        public bool showTranslation;
        public bool useChineseTranslation;
        [Min(0.5f)] public float fallbackWidth = 1.3f;
        [Min(0.5f)] public float fallbackDistance = 1.6f;
        [Tooltip("Offset toward the viewer to avoid z-fighting with the video.")]
        [Min(0)] public float screenGap = 0.015f;

        public event Action SpeakRequested;
        public event Action HintRequested;
        public event Action NextRequested;
        public RectTransform Panel => m_Panel;

        const float Width = 1400f;
        RectTransform m_Panel;
        Canvas m_Canvas;
        Text m_Japanese, m_Kana, m_Translation, m_Status, m_SpeakLabel;
        Button m_Speak, m_Hint, m_Next;
        LearningOverlayData m_Overlay;
        LearningFeedback m_Feedback;
        bool m_Busy, m_Listening, m_Done;
        string m_StatusOverride = "";
        float m_Height;
        float m_AvailableHeight = 380;

        void Awake() => BuildUi();
        void OnEnable() { BuildUi(); Refresh(); }
        void OnDisable() { if (m_Panel) m_Panel.gameObject.SetActive(false); }
        void OnDestroy() { if (m_Panel) Destroy(m_Panel.gameObject); }

        public void Show(LearningOverlayData overlay, LearningFeedback feedback, bool done)
        {
            BuildUi();
            m_Overlay = overlay; m_Feedback = feedback; m_Done = done;
            m_StatusOverride = "";
            Refresh();
        }

        public void SetStatus(string text) { BuildUi(); m_StatusOverride = text ?? ""; Refresh(); }
        public void SetBusy(bool busy) { BuildUi(); m_Busy = busy; Refresh(); }
        public void SetListening(bool listening) { BuildUi(); m_Listening = listening; Refresh(); }
        public void SetKanaVisible(bool visible) { showKana = visible; Refresh(); }
        public void SetTranslationVisible(bool visible) { showTranslation = visible; Refresh(); }
        public void SetChineseTranslation(bool chinese) { useChineseTranslation = chinese; Refresh(); }

        // Also callable from an external menu, hardware binding, or inspector UnityEvent.
        public void Speak() { if (m_Speak && m_Speak.interactable) SpeakRequested?.Invoke(); }
        public void Hint() { if (m_Hint && m_Hint.interactable) HintRequested?.Invoke(); }
        public void Next() { if (m_Next && m_Next.interactable) NextRequested?.Invoke(); }

        void BuildUi()
        {
            if (m_Panel) return;
            // A separate owned root keeps subtitle glyphs undistorted when the video or rig is scaled.
            var root = new GameObject("Learning Subtitles", typeof(RectTransform), typeof(Canvas),
                typeof(GraphicRaycaster), typeof(TrackedDeviceGraphicRaycaster));
            m_Panel = (RectTransform)root.transform;
            m_Panel.sizeDelta = new Vector2(Width, 200);
            m_Canvas = root.GetComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.WorldSpace;
            root.GetComponent<TrackedDeviceGraphicRaycaster>().checkFor3DOcclusion = true;
            var font = subtitleFont ? subtitleFont : Resources.Load<Font>("NotoSansCJKjp-Regular");
            if (!font)
            {
                font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                Debug.LogWarning("[Learning] Noto CJK font is missing; Japanese glyphs may be unavailable.");
            }
            var background = new GameObject("Subtitle Backdrop", typeof(Image));
            background.transform.SetParent(m_Panel, false);
            Stretch((RectTransform)background.transform);
            background.GetComponent<Image>().color = new Color(0.015f, 0.025f, 0.04f, 0.48f);
            background.GetComponent<Image>().raycastTarget = false;

            m_Japanese = MakeText("Japanese", font, 42, Color.white);
            m_Kana = MakeText("Kana", font, 29, new Color(0.82f, 0.9f, 0.94f));
            m_Translation = MakeText("Translation", font, 29, new Color(0.82f, 0.9f, 0.94f));
            m_Status = MakeText("Feedback", font, 25, new Color(0.68f, 0.84f, 0.83f));
            m_Speak = MakeButton("Speak", -205, font, out m_SpeakLabel);
            m_Hint = MakeButton("Hint", 0, font, out _);
            m_Next = MakeButton("Next", 205, font, out _);
            m_Speak.onClick.AddListener(Speak);
            m_Hint.onClick.AddListener(Hint);
            m_Next.onClick.AddListener(Next);
            Refresh();
        }

        Text MakeText(string name, Font font, int size, Color color)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(m_Panel, false);
            var text = go.GetComponent<Text>();
            text.font = font; text.fontSize = size; text.color = color;
            text.supportRichText = false; text.raycastTarget = false;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        Button MakeButton(string label, float x, Font font, out Text text)
        {
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(m_Panel, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0);
            rt.sizeDelta = new Vector2(180, 56);
            rt.anchoredPosition = new Vector2(x, 40);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.15f, 0.25f, 0.3f, 0.95f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            text = MakeText(label + " Label", font, 28, Color.white);
            text.transform.SetParent(rt, false);
            Stretch((RectTransform)text.transform);
            text.text = label;
            return button;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        void Refresh()
        {
            if (!m_Panel) return;
            m_Japanese.text = m_Overlay?.jp ?? (m_Done ? "お疲れさまでした" : "");
            m_Kana.text = showKana ? m_Overlay?.kana ?? "" : "";
            m_Translation.text = showTranslation && m_Overlay != null
                ? (useChineseTranslation ? m_Overlay.zh : m_Overlay.en) ?? "" : "";
            string status = m_StatusOverride;
            if (string.IsNullOrEmpty(status))
            {
                if (m_Listening) status = "Listening…";
                else if (m_Busy) status = "One moment…";
                else if (m_Done) status = "Lesson complete";
                else if (m_Feedback?.notes != null && m_Feedback.notes.Length > 0)
                    status = m_Feedback.notes[0] ?? "";
            }
            // Feedback is supplementary; preserve the target sentence's visual priority.
            m_Status.text = status.Length > 150 ? status.Substring(0, 147) + "…" : status;
            bool controls = m_Overlay != null && !m_Done;
            m_Speak.gameObject.SetActive(controls);
            m_Hint.gameObject.SetActive(controls);
            m_Next.gameObject.SetActive(controls);
            FitLayout(controls);
            m_SpeakLabel.text = m_Listening ? "Finish" : "Speak";
            m_Speak.interactable = !m_Busy && !m_Done && m_Overlay != null;
            m_Hint.interactable = m_Next.interactable = !m_Busy && !m_Listening && !m_Done && m_Overlay != null;
            bool content = controls || !string.IsNullOrEmpty(m_Japanese.text) || !string.IsNullOrEmpty(m_Status.text);
            m_Panel.gameObject.SetActive(isActiveAndEnabled && content);
        }

        void FitLayout(bool controls)
        {
            float footer = controls ? 80 : 12;
            float budget = Mathf.Max(1, m_AvailableHeight - footer - 12);
            m_Japanese.resizeTextForBestFit = false;
            m_Status.resizeTextForBestFit = false;
            m_Japanese.fontSize = 42;
            float japanese = MeasureLine(m_Japanese);
            float kana = MeasureLine(m_Kana);
            float translation = MeasureLine(m_Translation);
            float status = MeasureLine(m_Status);
            // Collapse secondary rows before reducing the primary sentence's type size.
            if (japanese + kana + translation + status > budget) translation = 0;
            if (japanese + kana + status > budget) kana = 0;
            if (japanese + status > budget && m_Overlay != null) status = 0;
            while (japanese + status > budget && m_Japanese.fontSize > 32)
            {
                m_Japanese.fontSize -= 2;
                japanese = MeasureLine(m_Japanese);
            }
            if (japanese + status > budget)
            {
                // Exceptional long external lessons still remain inside the video. Authored lines
                // normally fit at 42; let legacy Text fit the remaining rectangle when needed.
                m_Japanese.resizeTextForBestFit = true;
                m_Japanese.resizeTextMinSize = 22;
                m_Japanese.resizeTextMaxSize = 32;
                japanese = Mathf.Max(0, budget - status);
            }
            // An initial/join/error message has no target sentence; constrain it independently.
            if (japanese == 0 && status > budget)
            {
                m_Status.resizeTextForBestFit = true;
                m_Status.resizeTextMinSize = 20;
                m_Status.resizeTextMaxSize = 25;
                status = budget;
            }
            else m_Status.resizeTextForBestFit = false;
            float y = 12;
            LayoutLine(m_Japanese, ref y, japanese);
            LayoutLine(m_Kana, ref y, kana);
            LayoutLine(m_Translation, ref y, translation);
            LayoutLine(m_Status, ref y, status);
            m_Height = Mathf.Min(m_AvailableHeight, y + footer);
            m_Panel.sizeDelta = new Vector2(Width, m_Height);
        }

        static float MeasureLine(Text text)
        {
            if (string.IsNullOrEmpty(text.text)) return 0;
            var rt = (RectTransform)text.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 1);
            rt.offsetMin = new Vector2(36, -100);
            rt.offsetMax = new Vector2(-36, 0);
            // Noto's line metrics are taller than the built-in Latin font.
            return Mathf.Max(text.preferredHeight + 6, text.fontSize * 1.5f);
        }

        static void LayoutLine(Text text, ref float y, float height)
        {
            text.gameObject.SetActive(height > 0);
            if (height <= 0) return;
            var rt = (RectTransform)text.transform;
            rt.offsetMin = new Vector2(36, -y - height);
            rt.offsetMax = new Vector2(-36, -y);
            y += height;
        }

        void LateUpdate()
        {
            if (!m_Panel) return;
            if (!headCamera) headCamera = Camera.main;
            m_Canvas.worldCamera = headCamera;
            float width;
            float screenHeight;
            Vector3 bottom;
            Vector3 up;
            Quaternion rotation;
            if (screen)
            {
                var bounds = screen.localBounds;
                width = Mathf.Max(0.01f, screen.transform.TransformVector(Vector3.right * bounds.size.x).magnitude * 0.88f);
                screenHeight = Mathf.Max(0.01f, screen.transform.TransformVector(Vector3.up * bounds.size.y).magnitude);
                bottom = screen.transform.TransformPoint(new Vector3(bounds.center.x, bounds.min.y, bounds.center.z));
                up = screen.transform.TransformVector(Vector3.up).normalized;
                rotation = screen.transform.rotation;
                if (headCamera && Vector3.Dot(rotation * Vector3.forward, bottom - headCamera.transform.position) < 0)
                    rotation *= Quaternion.Euler(0, 180, 0);
            }
            else if (headCamera)
            {
                // A virtual 16:9 screen spanning at most 30 horizontal degrees stays in central FOV.
                float distance = Mathf.Max(0.5f, fallbackDistance);
                float virtualWidth = Mathf.Min(Mathf.Max(0.1f, fallbackWidth), 2 * distance * Mathf.Tan(15 * Mathf.Deg2Rad));
                width = virtualWidth * 0.88f;
                screenHeight = virtualWidth * 9 / 16;
                rotation = Quaternion.LookRotation(headCamera.transform.forward, headCamera.transform.up);
                up = rotation * Vector3.up;
                bottom = headCamera.transform.position + headCamera.transform.forward * distance - up * (screenHeight * 0.5f);
            }
            else return;
            float pixelScale = width / Width;
            // Bottom 3% margin + at most 42% subtitle height fits entirely in the lower 45%.
            float available = screenHeight * 0.42f / pixelScale;
            if (Mathf.Abs(available - m_AvailableHeight) > 0.5f)
            {
                m_AvailableHeight = available;
                Refresh();
            }
            Vector3 anchor = bottom + up * (screenHeight * 0.03f + m_Height * pixelScale * 0.5f);
            if (headCamera) anchor += (headCamera.transform.position - anchor).normalized * Mathf.Max(0.002f, screenGap);
            m_Panel.SetPositionAndRotation(anchor, rotation);
            m_Panel.localScale = Vector3.one * pixelScale;
        }
    }
}
