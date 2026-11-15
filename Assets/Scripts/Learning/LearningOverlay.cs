using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace ImmerseTheBay.Learning
{
    /// <summary>Stable, readable subtitles inside the shared video’s lower safe area. Buttons use the scene's XRI UI input module.</summary>
    public class LearningOverlay : MonoBehaviour
    {
        public Renderer screen;
        public Camera headCamera;
        [Tooltip("Optional override; otherwise loads the bundled Noto CJK font.")]
        public Font subtitleFont;
        public bool showRomaji = true;
        public bool showKana = true;
        public bool showTranslation;
        public bool useChineseTranslation;
        [Min(0.5f)] public float fallbackWidth = 1.3f;
        [Min(0.5f)] public float fallbackDistance = 1.6f;
        [Tooltip("Offset toward the viewer to avoid z-fighting with the video.")]
        [Min(0)] public float screenGap = 0.015f;

        public event Action ConversationRequested;
        public event Action ReturnRequested;
        public event Action SpeakRequested;
        public event Action HintRequested;
        public event Action NextRequested;
        public RectTransform Panel => m_Panel;
        public RectTransform ControlsPanel => m_ControlPanel;
        RectTransform m_ControlPanel;
        Canvas m_ControlCanvas;
        Pose? m_Tabletop;
        string[] m_FocusWords;
        public void PlaceControls(Pose pose) => m_Tabletop = pose;

        const float Width = 1400f;
        const float ReferenceHeight = 900f;
        const int JapaneseSize = 60;
        const int PronunciationSize = 40;
        const int TranslationSize = 36;
        const int DialogueSize = 40;
        RectTransform m_Panel;
        Canvas m_Canvas;
        Text m_Japanese, m_Romaji, m_Kana, m_Translation, m_Status, m_SpeakLabel, m_Clerk, m_Learner, m_ConversationLabel;
        Button m_Speak, m_Hint, m_Next, m_Conversation, m_Return;
        LearningOverlayData m_Overlay;
        LearningFeedback m_Feedback;
        bool m_Busy, m_Listening, m_Done, m_ConversationActive, m_AgentSpeaking, m_Detour, m_ControlsLocked;
        string m_ClerkCaption = "", m_ClerkEnglish = "", m_LearnerCaption = "", m_LearnerEnglish = "", m_RouteLabel = "", m_ConversationStatus = "";
        Func<string> m_Debug;
        string m_StatusOverride = "";
        string m_PracticeStatus = "";
        string m_PracticeProgress = "";
        float m_Height;
        float m_AvailableHeight = ReferenceHeight * 0.52f;

        void Awake()
        {
            m_Debug = () => $"<b>== LESSON PRESENTATION ==</b>\nconversation={m_ConversationActive} speaking={m_AgentSpeaking} detour={m_Detour} locked={m_ControlsLocked}";
            BuildUi();
        }
        void OnEnable() { DebugRegistry.Register(m_Debug); BuildUi(); Refresh(); }
        void OnDisable() { DebugRegistry.Unregister(m_Debug); if (m_Panel) m_Panel.gameObject.SetActive(false); if (m_ControlPanel) m_ControlPanel.gameObject.SetActive(false); }
        void OnDestroy() { if (m_Panel) Destroy(m_Panel.gameObject); if (m_ControlPanel) Destroy(m_ControlPanel.gameObject); }

        public void Show(LearningOverlayData overlay, LearningFeedback feedback, bool done, string[] focusWords = null)
        {
            BuildUi();
            m_FocusWords = focusWords;
            m_Overlay = overlay; m_Feedback = feedback; m_Done = done;
            m_StatusOverride = "";
            Refresh();
        }

        public void SetPracticeResult(string progress, string target, string result)
        {
            m_PracticeProgress = progress ?? "";
            m_PracticeStatus = string.IsNullOrEmpty(result) ? ""
                : result + " · " + progress + "\n" + Shorten(target ?? "", 24);
            Refresh();
        }

        public void SetStatus(string text) { BuildUi(); m_StatusOverride = text ?? ""; Refresh(); }
        public void SetBusy(bool busy) { BuildUi(); m_Busy = busy; Refresh(); }
        public void SetListening(bool listening) { BuildUi(); m_Listening = listening; Refresh(); }
        public void SetRomajiVisible(bool visible) { showRomaji = visible; Refresh(); }
        public void SetKanaVisible(bool visible) { showKana = visible; Refresh(); }
        public void SetTranslationVisible(bool visible) { showTranslation = visible; Refresh(); }
        public void SetChineseTranslation(bool chinese) { useChineseTranslation = chinese; Refresh(); }

        public void SetConversationState(bool connected, bool speaking, string status = null)
        {
            BuildUi(); m_ConversationActive = connected; m_AgentSpeaking = speaking;
            m_ConversationStatus = status ?? ""; Refresh();
        }
        public void SetRoute(bool isDetour, string label = null)
        { BuildUi(); m_Detour = isDetour; m_RouteLabel = label ?? ""; Refresh(); }
        public void SetClerkCaption(string text, string english = null)
        { BuildUi(); m_ClerkCaption = text ?? ""; m_ClerkEnglish = english ?? ""; Refresh(); }
        public void SetLearnerCaption(string text, string english = null)
        { BuildUi(); m_LearnerCaption = text ?? ""; m_LearnerEnglish = english ?? ""; Refresh(); }

        static string Dialogue(string label, string original, string english) =>
            string.IsNullOrEmpty(original) ? "" :
            label + " · " + original + (string.IsNullOrEmpty(english) ? "" : "\n" + english);
        public void SetControlsLocked(bool locked) { BuildUi(); m_ControlsLocked = locked; Refresh(); }
        public void Conversation() { if (m_Conversation && m_Conversation.interactable) ConversationRequested?.Invoke(); }
        public void ReturnToLesson() { if (m_Return && m_Return.interactable) ReturnRequested?.Invoke(); }

        // Also callable from an external menu, hardware binding, or inspector UnityEvent.
        public void Speak() { if (m_Speak && m_Speak.interactable) SpeakRequested?.Invoke(); }
        public void Hint() { if (m_Hint && m_Hint.interactable) HintRequested?.Invoke(); }
        public void Next() { if (m_Next && m_Next.interactable) NextRequested?.Invoke(); }

        void BuildUi()
        {
            if (m_Panel) return;
            // A separate owned root keeps subtitle glyphs undistorted when the video or rig is scaled.
            var root = new GameObject("Learning Subtitles", typeof(RectTransform), typeof(Canvas));
            m_Panel = (RectTransform)root.transform;
            m_Panel.sizeDelta = new Vector2(Width, 200);
            m_Canvas = root.GetComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.WorldSpace;
            m_Canvas.sortingOrder = 20;
            var controls = new GameObject("Learning Buttons", typeof(RectTransform), typeof(Canvas),
                typeof(GraphicRaycaster), typeof(TrackedDeviceGraphicRaycaster));
            m_ControlPanel = (RectTransform)controls.transform;
            m_ControlPanel.sizeDelta = new Vector2(1240, 160);
            m_ControlCanvas = controls.GetComponent<Canvas>();
            m_ControlCanvas.renderMode = RenderMode.WorldSpace;
            m_ControlCanvas.sortingOrder = 21;
            controls.GetComponent<TrackedDeviceGraphicRaycaster>().checkFor3DOcclusion = true;
            var font = subtitleFont ? subtitleFont : Resources.Load<Font>("NotoSansCJKjp-Regular");
            if (!font)
            {
                font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                Debug.LogWarning("[Learning] Noto CJK font is missing; Japanese glyphs may be unavailable.");
            }
            var background = new GameObject("Subtitle Backdrop", typeof(Image));
            background.transform.SetParent(m_Panel, false);
            Stretch((RectTransform)background.transform);
            background.GetComponent<Image>().color = new Color(0.015f, 0.025f, 0.04f, 0.72f);
            background.GetComponent<Image>().raycastTarget = false;

            m_Clerk = MakeText("Clerk", font, DialogueSize, new Color(0.82f, 0.9f, 0.94f));
            m_Learner = MakeText("Learner", font, DialogueSize, new Color(0.78f, 0.94f, 0.82f));
            m_Japanese = MakeText("Japanese", font, JapaneseSize, Color.white);
            m_Japanese.supportRichText = true;
            m_Romaji = MakeText("Romaji", font, PronunciationSize, new Color(0.9f, 0.94f, 0.96f));
            m_Kana = MakeText("Kana", font, TranslationSize, new Color(0.82f, 0.9f, 0.94f));
            m_Translation = MakeText("Translation", font, TranslationSize, new Color(0.82f, 0.9f, 0.94f));
            m_Status = MakeText("Feedback", font, 25, new Color(0.68f, 0.84f, 0.83f));
            m_Conversation = MakeButton("Conversation", 0, font, out m_ConversationLabel);
            m_Return = MakeButton("Return to lesson", 0, font, out _);
            m_Conversation.onClick.AddListener(Conversation);
            m_Return.onClick.AddListener(ReturnToLesson);
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
            text.resizeTextForBestFit = false;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        Button MakeButton(string label, float x, Font font, out Text text)
        {
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(m_ControlPanel, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0);
            rt.sizeDelta = new Vector2(228, 120);
            rt.anchoredPosition = new Vector2(x, 40);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.15f, 0.25f, 0.3f, 1f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            text = MakeText(label + " Label", font, 32, Color.white);
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
            m_Clerk.text = Dialogue("店員", m_ClerkCaption, m_ClerkEnglish);
            m_Learner.text = Dialogue("あなた", m_LearnerCaption, m_LearnerEnglish);
            m_Japanese.text = m_ConversationActive ? "" : Highlight(m_Overlay?.jp ?? (m_Done ? "お疲れさまでした" : ""));
            m_Romaji.text = !m_ConversationActive && showRomaji ? m_Overlay?.romaji ?? "" : "";
            m_Kana.text = !m_ConversationActive && showKana ? m_Overlay?.kana ?? "" : "";
            m_Translation.text = !m_ConversationActive && showTranslation && m_Overlay != null
                ? (useChineseTranslation ? m_Overlay.zh : m_Overlay.en) ?? "" : "";
            string status = m_StatusOverride;
            if (!m_ConversationActive && string.IsNullOrEmpty(status)) status = m_PracticeStatus;
            if (string.IsNullOrEmpty(status))
            {
                if (m_ConversationActive) status = !string.IsNullOrEmpty(m_ConversationStatus) ? m_ConversationStatus
                    : m_AgentSpeaking ? "Clerk speaking · you can interrupt" : "Listening · speak naturally";
                else if (m_Listening) status = "Listening…";
                else if (m_Busy) status = "One moment…";
                else if (m_Done) status = "Lesson complete";
                else if (m_Detour) status = string.IsNullOrEmpty(m_RouteLabel) ? "Exploring · return when ready" : Shorten(m_RouteLabel, 65);
                else if (m_Feedback?.notes != null && m_Feedback.notes.Length > 0)
                    status = m_Feedback.notes[0] ?? "";
            }
            if (!m_ConversationActive && m_PracticeStatus.Length == 0 && m_PracticeProgress.Length > 0)
                status = string.IsNullOrWhiteSpace(status) ? m_PracticeProgress : m_PracticeProgress + "\n" + status;
            // Feedback is supplementary; preserve the target sentence's visual priority.
            m_Status.text = status.Length > 150 ? status.Substring(0, 147) + "…" : status;
            bool controls = m_Overlay != null && !m_Done;
            m_Conversation.gameObject.SetActive(controls || m_ConversationActive);
            bool manual = controls && !m_ConversationActive;
            m_Speak.gameObject.SetActive(manual);
            m_Hint.gameObject.SetActive(manual);
            m_Next.gameObject.SetActive(manual);
            m_Return.gameObject.SetActive(controls && m_Detour);
            LayoutButtons();
            FitLayout();
            m_ControlPanel.gameObject.SetActive(isActiveAndEnabled && (controls || m_ConversationActive));
            m_ConversationLabel.text = m_ConversationActive ? "End conversation" : "Conversation";
            m_Conversation.interactable = m_ConversationActive || (controls && !m_Busy && !m_Listening && !m_ControlsLocked);
            m_SpeakLabel.text = m_Listening ? "Finish" : "Speak";
            m_Speak.interactable = manual && !m_Busy && !m_ControlsLocked;
            m_Hint.interactable = m_Next.interactable = manual && !m_Busy && !m_Listening && !m_ControlsLocked;
            m_Return.interactable = controls && !m_Busy && !m_Listening && !m_ControlsLocked && !m_ConversationActive;
            bool content = controls || m_ConversationActive || !string.IsNullOrEmpty(m_Japanese.text) || !string.IsNullOrEmpty(m_Status.text) || m_Clerk.text.Length > 0 || m_Learner.text.Length > 0;
            m_Panel.gameObject.SetActive(isActiveAndEnabled && content);
        }

        string Highlight(string text)
        {
            var result = new System.Text.StringBuilder();
            for (int offset = 0; offset < text.Length;)
            {
                string match = null;
                if (m_FocusWords != null)
                    foreach (var word in m_FocusWords)
                        if (!string.IsNullOrEmpty(word) && word.Length > (match?.Length ?? 0) &&
                            text.IndexOf(word, offset, System.StringComparison.Ordinal) == offset) match = word;
                if (match != null)
                {
                    result.Append("<color=#FFE28A>").Append(EscapeRichText(match)).Append("</color>");
                    offset += match.Length;
                }
                else result.Append(EscapeRichText(text.Substring(offset++, 1)));
            }
            return result.ToString();
        }

        static string EscapeRichText(string text) => text.Replace('<', '＜').Replace('>', '＞');

        static string Shorten(string text, int length) => text.Length <= length ? text : text.Substring(0, length - 1) + "…";

        void LayoutButtons()
        {
            var buttons = new[] { m_Conversation, m_Speak, m_Hint, m_Next, m_Return };
            int count = 0;
            foreach (var button in buttons) if (button.gameObject.activeSelf) count++;
            int index = 0;
            foreach (var button in buttons)
                if (button.gameObject.activeSelf)
                    ((RectTransform)button.transform).anchoredPosition = new Vector2((index++ - (count - 1) * 0.5f) * 244, 80);
        }

        void FitLayout()
        {
            const float padding = 12;
            float budget = Mathf.Max(1, m_AvailableHeight - padding * 2);
            float japanese = MeasureLine(m_Japanese);
            float romaji = MeasureLine(m_Romaji);
            float translation = MeasureLine(m_Translation);
            float clerk = MeasureLine(m_Clerk);
            float learner = MeasureLine(m_Learner);
            float kana = MeasureLine(m_Kana);
            float status = MeasureLine(m_Status);

            // Never reduce glyph size as captions arrive or wrap. Keep the current
            // lesson's Japanese, romaji and requested translation ahead of history/hints.
            if (japanese + romaji + translation + clerk + learner + kana + status > budget) kana = 0;
            if (japanese + romaji + translation + clerk + learner + status > budget && japanese + romaji > 0)
                clerk = learner = 0;
            if (japanese + romaji + translation + clerk + learner + status > budget)
                status = !m_ConversationActive && (m_PracticeStatus.Length > 0 || m_PracticeProgress.Length > 0)
                    ? MinimumLineHeight(m_Status) : 0;
            float textBudget = budget - status;

            float required = japanese + romaji + translation + clerk + learner;
            if (required > textBudget)
            {
                // External transcripts can be arbitrarily long. Bound their rows and
                // show an explicit ellipsis at the same font size instead of tiny type.
                float minJapanese = MinimumLineHeight(m_Japanese);
                float minRomaji = MinimumLineHeight(m_Romaji);
                float minTranslation = MinimumLineHeight(m_Translation);
                float minClerk = clerk > 0 ? MinimumLineHeight(m_Clerk) : 0;
                float minLearner = learner > 0 ? MinimumLineHeight(m_Learner) : 0;
                float minimum = minJapanese + minRomaji + minTranslation + minClerk + minLearner;
                float share = Mathf.Clamp01((textBudget - minimum) / Mathf.Max(1, required - minimum));
                japanese = minJapanese + (japanese - minJapanese) * share;
                romaji = minRomaji + (romaji - minRomaji) * share;
                translation = minTranslation + (translation - minTranslation) * share;
                clerk = minClerk + (clerk - minClerk) * share;
                learner = minLearner + (learner - minLearner) * share;
            }
            float y = padding;
            LayoutCaption(m_Clerk, ref y, clerk);
            LayoutCaption(m_Learner, ref y, learner);
            LayoutCaption(m_Japanese, ref y, japanese);
            LayoutCaption(m_Romaji, ref y, romaji);
            LayoutCaption(m_Translation, ref y, translation);
            LayoutCaption(m_Kana, ref y, kana);
            LayoutCaption(m_Status, ref y, status);
            m_Height = Mathf.Min(m_AvailableHeight, y + padding);
            m_Panel.sizeDelta = new Vector2(Width, m_Height);
        }

        void LayoutCaption(Text text, ref float y, float height)
        {
            LayoutLine(text, ref y, height);
            if (height <= 0 || text.preferredHeight + 6 <= height) return;
            // Japanese is the only rich-text row; truncate the source before adding
            // vocabulary highlights so an ellipsis can never leave a broken color tag.
            string source = text == m_Japanese
                ? m_Overlay?.jp ?? (m_Done ? "お疲れさまでした" : "")
                : text.text;
            var boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(source);
            int low = 0, high = boundaries.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                int end = mid < boundaries.Length ? boundaries[mid] : source.Length;
                string candidate = source.Substring(0, end).TrimEnd() + "…";
                text.text = text == m_Japanese ? Highlight(candidate) : candidate;
                if (text.preferredHeight + 6 <= height) low = mid;
                else high = mid - 1;
            }
            int length = low < boundaries.Length ? boundaries[low] : source.Length;
            string fitted = source.Substring(0, length).TrimEnd() + "…";
            text.text = text == m_Japanese ? Highlight(fitted) : fitted;
        }

        static float MinimumLineHeight(Text text)
        {
            if (string.IsNullOrEmpty(text.text)) return 0;
            string source = text.text;
            text.text = "…";
            float height = MeasureLine(text);
            text.text = source;
            return height;
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
            m_ControlCanvas.worldCamera = headCamera;
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
            // Uniform scale preserves glyph proportions. Height caps the type size on
            // wide video; width caps it on narrow video. Resolution changes at the
            // same aspect cannot change typography, and ultrawide media cannot inflate it.
            float pixelScale = Mathf.Min(width / Width, screenHeight / ReferenceHeight);
            // The band grows only as needed, up to the lower 52%; controls have their
            // own tabletop canvas. Keep the upper scene visible even for long captions.
            float available = screenHeight * 0.52f / pixelScale;
            if (Mathf.Abs(available - m_AvailableHeight) > 0.5f)
            {
                m_AvailableHeight = available;
                Refresh();
            }
            Vector3 anchor = bottom + up * (screenHeight * 0.03f + m_Height * pixelScale * 0.5f);
            if (headCamera) anchor += (headCamera.transform.position - anchor).normalized * Mathf.Max(0.002f, screenGap);
            m_Panel.SetPositionAndRotation(anchor, rotation);
            m_Panel.localScale = Vector3.one * pixelScale;
            if (headCamera)
            {
                var heading = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up).normalized;
                if (heading.sqrMagnitude < 0.01f) return;
                var upright = Quaternion.LookRotation(heading, Vector3.up);
                // Face the user from arm's reach and remain below eye level when they look down.
                var pose = m_Tabletop ?? new Pose(
                    headCamera.transform.position + heading * 0.57f - Vector3.up * 0.29f,
                    upright * Quaternion.Euler(28, 0, 0));
                m_ControlPanel.SetPositionAndRotation(pose.position, pose.rotation);
                m_ControlPanel.localScale = Vector3.one * 0.00039f;
            }
        }
    }
}
