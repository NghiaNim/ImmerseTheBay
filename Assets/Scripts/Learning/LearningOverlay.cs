using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
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
        public bool showKana;
        public bool showTranslation = true;
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
        public event Action ExploreRequested;
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
        const float ControlButtonSpacing = 248f;
        const float CaptionBottom = 0.055f;
        const float CaptionSidePadding = 36f;
        const int GroupReadingSize = 25;
        const int MaximumReadingGroups = 24;
        RectTransform m_Panel;
        RectTransform m_Hud, m_InstructionText, m_ReadingPanel;
        Canvas m_Canvas;
        Text m_Japanese, m_Romaji, m_Kana, m_Translation, m_Status, m_SpeakLabel, m_Clerk, m_Learner, m_ConversationLabel;
        Text m_ProgressBadge, m_ResultBadge, m_Instruction, m_ControlHint, m_LookDown, m_ExploreLabel, m_ExploreMarker;
        readonly List<(Text japanese, Text kana, Text romaji, RectTransform underline)> m_ReadingCells = new();
        GameObject m_Backdrop;
        readonly RectTransform[] m_Waveform = new RectTransform[5];
        readonly float[] m_ListeningHistory = new float[5];
        Button m_Speak, m_Hint, m_Next, m_Conversation, m_Return, m_Explore;
        bool m_ExploreAvailable, m_ExploreActive;
        Vector3? m_ExploreAim;
        string m_ExploreHint;
        public bool HoveringLessonAction => m_HoveredButton && m_HoveredButton.isActiveAndEnabled && m_HoveredButton.interactable;
        Button[] m_Buttons;
        Button m_HoveredButton;
        bool m_SceneTransition;
        static readonly Color ActionPurple = new Color32(70, 48, 146, 245);
        static readonly Color HoverPurple = new Color32(130, 98, 236, 255);
        LearningOverlayData m_Overlay;
        LearningFeedback m_Feedback;
        bool m_Busy, m_Listening, m_Done, m_ConversationActive, m_AgentSpeaking, m_Detour, m_ControlsLocked;
        bool m_ClerkSpeaking, m_ExampleSpeaking;
        string m_ClerkCaption = "", m_ClerkEnglish = "", m_ClerkRomaji = "", m_LearnerCaption = "", m_LearnerEnglish = "", m_RouteLabel = "", m_ConversationStatus = "";
        Func<string> m_Debug;
        string m_StatusOverride = "";
        string m_PracticeStatus = "";
        string m_PracticeProgress = "";
        string m_PracticeOutcome = "";
        string m_PracticeAttemptId, m_CompletionSummary = "";
        LearningAttempt m_ResultAttempt;
        bool m_ShowCorrection;
        float m_ListeningLevel, m_NextWaveformSample, m_LastListeningSample;
        float m_ResultEmphasisUntil;
        float m_Height;
        float m_ReadingHeight;
        float m_AvailableHeight = ReferenceHeight * 0.38f;

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
            m_SceneTransition = false;
            m_StatusOverride = "";
            Refresh();
        }

        public void SetPracticeResult(string progress, string result, string outcome, string attemptId = null,
            LearningAttempt currentAttempt = null)
        {
            if (!string.IsNullOrEmpty(result) && (result != m_PracticeStatus || attemptId != m_PracticeAttemptId))
                m_ResultEmphasisUntil = Time.unscaledTime + 1.8f;
            m_PracticeProgress = progress ?? "";
            m_PracticeStatus = result ?? "";
            m_PracticeOutcome = outcome ?? "";
            m_PracticeAttemptId = attemptId;
            m_ResultAttempt = currentAttempt;
            Refresh();
        }

        public void SetCompletionSummary(string text)
        {
            m_CompletionSummary = text ?? "";
            Refresh();
        }

        public void SetStatus(string text) { BuildUi(); m_StatusOverride = text ?? ""; Refresh(); }
        public void SetBusy(bool busy) { BuildUi(); m_Busy = busy; if (!busy) m_SceneTransition = false; Refresh(); }
        public void SetSceneTransition(bool changing) { BuildUi(); m_SceneTransition = changing; Refresh(); }
        public void SetListening(bool listening)
        {
            BuildUi();
            if (m_Listening == listening) return;
            m_Listening = listening;
            m_ListeningLevel = m_NextWaveformSample = m_LastListeningSample = 0;
            Array.Clear(m_ListeningHistory, 0, m_ListeningHistory.Length);
            foreach (var bar in m_Waveform) bar.sizeDelta = new Vector2(8, 4);
            Refresh();
        }
        public void SetListeningLevel(float normalized)
        {
            if (!m_Listening || float.IsNaN(normalized)) return;
            m_ListeningLevel = Mathf.Clamp01(normalized);
            m_LastListeningSample = Time.unscaledTime;
        }
        public void SetExampleSpeaking(bool speaking)
        {
            if (m_ExampleSpeaking == speaking) return;
            m_ExampleSpeaking = speaking;
            Refresh();
        }
        public void SetClerkSpeaking(bool speaking)
        {
            if (m_ClerkSpeaking == speaking) return;
            m_ClerkSpeaking = speaking;
            Refresh();
        }
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
        public void SetClerkCaption(string text, string english = null, string romaji = null)
        {
            BuildUi();
            if (m_ClerkCaption != text) m_ClerkRomaji = "";
            m_ClerkCaption = text ?? "";
            m_ClerkEnglish = english ?? "";
            m_ClerkRomaji = romaji ?? m_ClerkRomaji;
            Refresh();
        }
        public void SetLearnerCaption(string text, string english = null)
        { BuildUi(); m_LearnerCaption = text ?? ""; m_LearnerEnglish = english ?? ""; Refresh(); }

        static string Dialogue(string label, string original, string english, string romaji = null) =>
            string.IsNullOrEmpty(original) ? "" :
            label + " · " + original + (string.IsNullOrEmpty(romaji) ? "" : "\n" + romaji)
                + (string.IsNullOrEmpty(english) ? "" : "\n" + english);

        static bool RepeatsTarget(string original, string target) =>
            !string.IsNullOrWhiteSpace(original) && !string.IsNullOrWhiteSpace(target)
            && string.Equals(original.Trim().TrimEnd('。', '？', '！', '.', '?', '!'),
                target.Trim().TrimEnd('。', '？', '！', '.', '?', '!'), StringComparison.Ordinal);
        public void SetExploreState(bool available, bool active, Vector3? aim, string hint)
        {
            m_ExploreAim = aim;
            if (available == m_ExploreAvailable && active == m_ExploreActive && hint == m_ExploreHint) return;
            m_ExploreAvailable = available;
            m_ExploreActive = active;
            m_ExploreHint = hint;
            Refresh();
        }

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
            m_ControlPanel.sizeDelta = new Vector2(1240, 300);
            m_ControlPanel.pivot = new Vector2(0.5f, 0f);
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
            m_Backdrop = background;

            m_Clerk = MakeText("Clerk", font, DialogueSize, new Color(0.82f, 0.9f, 0.94f));
            m_Learner = MakeText("Learner", font, DialogueSize, new Color(0.78f, 0.94f, 0.82f));
            m_Japanese = MakeText("Japanese", font, JapaneseSize, Color.white);
            m_Japanese.supportRichText = true;
            m_Romaji = MakeText("Romaji", font, PronunciationSize, new Color(0.9f, 0.94f, 0.96f));
            m_ReadingPanel = (RectTransform)new GameObject("Word readings", typeof(RectTransform)).transform;
            m_ReadingPanel.SetParent(m_Panel, false);
            m_ReadingPanel.anchorMin = new Vector2(0, 1);
            m_ReadingPanel.anchorMax = Vector2.one;
            m_ReadingPanel.pivot = new Vector2(0.5f, 1);
            m_ReadingPanel.gameObject.SetActive(false);
            m_Kana = MakeText("Kana", font, TranslationSize, new Color(0.82f, 0.9f, 0.94f));
            m_Translation = MakeText("Translation", font, TranslationSize, new Color(0.82f, 0.9f, 0.94f));
            m_Status = MakeText("Feedback", font, 25, new Color(0.68f, 0.84f, 0.83f));
            m_Hud = (RectTransform)new GameObject("Lesson HUD", typeof(RectTransform)).transform;
            m_Hud.SetParent(m_Panel, false);
            m_Hud.anchorMin = m_Hud.anchorMax = m_Hud.pivot = new Vector2(0.5f, 0f);
            m_ProgressBadge = MakeHudBadge("Progress", font, 28, new Vector2(1, 1), new Vector2(-30, -22), new Vector2(405, 57));
            m_ResultBadge = MakeHudBadge("Sentence result", font, 44, new Vector2(0.5f, 0.78f), Vector2.zero, new Vector2(880, 112));
            m_Instruction = MakeHudBadge("Instruction", font, 27, new Vector2(1, 0), new Vector2(-30, 3), new Vector2(530, 45));
            m_LookDown = MakeHudBadge("Look down", font, 27, new Vector2(0.5f, 0), new Vector2(-48, -8), new Vector2(385, 44));
            m_InstructionText = (RectTransform)m_Instruction.transform;
            m_ExploreMarker = MakeText("Explore target", font, 57, new Color(0.85f, 0.72f, 1f));
            var markerRect = m_ExploreMarker.rectTransform;
            markerRect.SetParent(m_Hud, false);
            markerRect.anchorMin = markerRect.anchorMax = new Vector2(0.5f, 0.5f);
            markerRect.sizeDelta = new Vector2(86, 90);
            m_ExploreMarker.alignment = TextAnchor.MiddleCenter;
            m_ExploreMarker.text = "＋";
            m_ExploreMarker.gameObject.SetActive(false);
            for (int i = 0; i < m_Waveform.Length; i++)
            {
                var bar = new GameObject("Input level", typeof(Image)).GetComponent<Image>();
                var rect = bar.rectTransform;
                rect.SetParent(m_Instruction.transform.parent, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0, 0.5f);
                rect.anchoredPosition = new Vector2(20 + 14 * i, 0);
                rect.sizeDelta = new Vector2(8, 4);
                bar.color = new Color(0.58f, 0.92f, 0.82f);
                bar.raycastTarget = false;
                m_Waveform[i] = rect;
            }
            m_ControlHint = MakeText("Button hint", font, 31, new Color(0.9f, 0.96f, 1f));
            var hintRect = (RectTransform)m_ControlHint.transform;
            hintRect.SetParent(m_ControlPanel, false);
            hintRect.anchorMin = hintRect.anchorMax = new Vector2(0.5f, 0);
            hintRect.sizeDelta = new Vector2(870, 54);
            m_ControlHint.text = "Point and pinch · or tap Speak on your phone";
            m_Conversation = MakeButton("Conversation", font, out m_ConversationLabel);
            m_Return = MakeButton("Return to lesson", font, out _);
            m_Conversation.onClick.AddListener(Conversation);
            m_Return.onClick.AddListener(ReturnToLesson);
            m_Speak = MakeButton("Speak", font, out m_SpeakLabel);
            m_Hint = MakeButton("Hint", font, out _);
            m_Next = MakeButton("Next", font, out _);
            m_Explore = MakeButton("Explore", font, out m_ExploreLabel);
            m_Explore.onClick.AddListener(() => ExploreRequested?.Invoke());
            m_Buttons = new[] { m_Conversation, m_Speak, m_Hint, m_Next, m_Explore };
            m_Speak.onClick.AddListener(Speak);
            m_Hint.onClick.AddListener(Hint);
            m_Next.onClick.AddListener(Next);
            foreach (var laser in FindObjectsByType<XRInteractorLineVisual>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                laser.lineWidth = Mathf.Max(0.007f, laser.lineWidth);
                laser.validColorGradient = RayColor(new Color32(183, 152, 255, 255), 1f);
                laser.invalidColorGradient = RayColor(new Color32(217, 204, 255, 255), 0.7f);
            }
            Refresh();
        }

        static Gradient RayColor(Color color, float opacity) => new Gradient
        {
            colorKeys = new[] { new GradientColorKey(color, 0), new GradientColorKey(Color.white, 1) },
            alphaKeys = new[] { new GradientAlphaKey(opacity * 0.55f, 0), new GradientAlphaKey(opacity, 1) }
        };

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

        Text MakeHudBadge(string name, Font font, int size, Vector2 corner, Vector2 offset, Vector2 dimensions)
        {
            var image = new GameObject(name, typeof(Image)).GetComponent<Image>();
            var rect = image.rectTransform;
            rect.SetParent(m_Hud, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = corner;
            rect.anchoredPosition = offset;
            rect.sizeDelta = dimensions;
            image.color = new Color(0.015f, 0.025f, 0.04f, 0.83f);
            image.raycastTarget = false;
            var text = MakeText(name + " text", font, size, Color.white);
            text.transform.SetParent(rect, false);
            var textRect = (RectTransform)text.transform;
            Stretch(textRect);
            textRect.offsetMin = new Vector2(12, 0);
            textRect.offsetMax = new Vector2(-12, 0);
            return text;
        }

        Button MakeButton(string label, Font font, out Text text)
        {
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(m_ControlPanel, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0);
            rt.sizeDelta = new Vector2(206, 136);
            var image = go.GetComponent<Image>();
            image.color = ActionPurple;
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var trigger = go.AddComponent<EventTrigger>();
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => { if (button.interactable) m_HoveredButton = button; });
            trigger.triggers.Add(enter);
            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ => { if (m_HoveredButton == button) m_HoveredButton = null; });
            trigger.triggers.Add(exit);
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
            m_ShowCorrection = ShouldShowCorrection();
            bool clerkTurn = !m_SceneTransition && !m_ConversationActive && m_ClerkSpeaking && m_ClerkCaption.Length > 0;
            bool targetTurn = !m_SceneTransition && !m_ConversationActive && !clerkTurn;
            bool showLearner = !m_SceneTransition && (m_ConversationActive || (targetTurn && !m_ExampleSpeaking
                && !m_ShowCorrection && m_PracticeStatus.Length > 0 && !RepeatsTarget(m_LearnerCaption, m_Overlay?.jp)));
            m_Clerk.text = !m_SceneTransition && m_ConversationActive ? Dialogue("店員", m_ClerkCaption, m_ClerkEnglish, m_ClerkRomaji)
                : clerkTurn ? Dialogue("店員", m_ClerkCaption, "") : "";
            m_Learner.text = showLearner ? Dialogue("あなた", m_LearnerCaption, m_LearnerEnglish) : "";
            m_Japanese.text = m_SceneTransition ? "Preparing next scene…" :
                targetTurn ? Highlight(m_Overlay?.jp ?? (m_Done ? "お疲れさまでした" : "")) : "";
            m_Romaji.text = clerkTurn ? m_ClerkRomaji : targetTurn && showRomaji ? m_Overlay?.romaji ?? "" : "";
            m_Kana.text = targetTurn && showKana ? m_Overlay?.kana ?? "" : "";
            m_Translation.text = clerkTurn ? m_ClerkEnglish : targetTurn && m_Done ? m_CompletionSummary
                : targetTurn && showTranslation && m_Overlay != null
                    ? (useChineseTranslation ? m_Overlay.zh : m_Overlay.en) ?? "" : "";
            m_ReadingHeight = targetTurn && showRomaji ? LayoutReadingGroups(m_Overlay?.readingGroups) : 0f;
            if (m_ReadingHeight > 0f)
                m_Japanese.text = m_Kana.text = m_Romaji.text = "";
            bool controls = m_Overlay != null && !m_Done && !m_SceneTransition;
            string status = PracticeInstruction();
            bool showCorner = controls || m_ConversationActive || m_Done;
            m_Status.text = showCorner ? "" : Shorten(status, 150);
            SetBadge(m_Instruction, showCorner ? Shorten(status, 46) : "");
            SetBadge(m_LookDown, controls ? "↓  Look down for actions" : "");
            SetBadge(m_ProgressBadge, !m_ConversationActive ? Shorten(m_PracticeProgress, 28) : "");
            UpdateResultBadge();
            bool showWaveform = m_Listening && controls && !m_ExampleSpeaking && !clerkTurn;
            m_InstructionText.offsetMin = new Vector2(showWaveform ? 90 : 12, 0);
            foreach (var bar in m_Waveform) bar.gameObject.SetActive(showWaveform);
            m_Conversation.gameObject.SetActive(controls || m_ConversationActive);
            bool manual = controls && !m_ConversationActive;
            m_Speak.gameObject.SetActive(manual);
            m_Hint.gameObject.SetActive(manual);
            m_Next.gameObject.SetActive(manual);
            m_Explore.gameObject.SetActive(manual && !m_Detour);
            m_ExploreLabel.text = m_ExploreActive ? "Cancel" : "Explore";
            m_Explore.interactable = manual && m_ExploreAvailable && !m_ControlsLocked;
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
            bool content = m_SceneTransition || showCorner || m_Status.text.Length > 0 || m_PracticeStatus.Length > 0;
            m_Panel.gameObject.SetActive(isActiveAndEnabled && content);
        }

        string PracticeInstruction()
        {
            if (m_SceneTransition) return "";
            if (m_ConversationActive) return !string.IsNullOrEmpty(m_ConversationStatus) ? m_ConversationStatus
                : m_AgentSpeaking ? "Clerk speaking · you can interrupt" : "Listening · speak naturally";
            if (!string.IsNullOrEmpty(m_ExploreHint)) return m_ExploreHint;
            if (m_ExampleSpeaking) return "Listen, then repeat";
            if (m_Listening) return m_StatusOverride == "Finishing speech…" ? m_StatusOverride : "Speak the Japanese sentence.";
            if (m_ClerkSpeaking) return "Clerk speaking";
            if (!string.IsNullOrEmpty(m_StatusOverride)) return m_StatusOverride;
            if (m_Busy) return "One moment…";
            if (m_Done) return "Lesson complete";
            if (m_Detour) return string.IsNullOrEmpty(m_RouteLabel) ? "Exploring · return when ready" : m_RouteLabel;
            if (m_Feedback?.notes != null && m_Feedback.notes.Length > 0)
            {
                string note = m_Feedback.notes[0] ?? "";
                if (!RepeatsTarget(note, m_Overlay?.jp)) return note;
            }
            return "";
        }

        static void SetBadge(Text text, string value)
        {
            text.text = value;
            text.transform.parent.gameObject.SetActive(value.Length > 0);
        }

        void UpdateResultBadge()
        {
            string result = m_PracticeStatus;
            bool passed = m_PracticeOutcome == "pass" || m_PracticeOutcome == "assisted";
            bool retry = m_PracticeOutcome == "retry";
            var background = m_ResultBadge.transform.parent.GetComponent<Image>();
            background.color = passed ? new Color(0.07f, 0.28f, 0.18f, 0.94f)
                : retry ? new Color(0.30f, 0.18f, 0.06f, 0.94f) : new Color(0.015f, 0.025f, 0.04f, 0.83f);
            m_ResultBadge.fontSize = m_ShowCorrection ? 34 : 44;
            if (m_ShowCorrection)
            {
                SetBadge(m_ResultBadge, $"Practice 「{m_ResultAttempt.correction.jp}」\nHeard {Shorten(m_ResultAttempt.text ?? "", 19)}");
                return;
            }
            SetBadge(m_ResultBadge, result.Length == 0 ? "" : (passed ? "✓  " : retry ? "↻  " : "") + result);
        }

        bool ShouldShowCorrection() =>
            m_PracticeOutcome == "retry" && !m_ConversationActive && !m_Done && !m_SceneTransition &&
            !(m_ClerkSpeaking && m_ClerkCaption.Length > 0) && Time.unscaledTime >= m_ResultEmphasisUntil &&
            m_ResultAttempt?.correction != null &&
            string.Equals(m_ResultAttempt.jp, m_Overlay?.jp, StringComparison.Ordinal);

        string Highlight(string text, int sentenceOffset = 0)
        {
            var correction = m_ShowCorrection ? m_ResultAttempt.correction : null;
            if (correction != null && correction.start >= sentenceOffset && correction.end <= sentenceOffset + text.Length)
            {
                int start = correction.start - sentenceOffset;
                int end = correction.end - sentenceOffset;
                return HighlightVocabulary(text.Substring(0, start)) +
                    "<b><color=#FFAE76>" + EscapeRichText(text.Substring(start, end - start)) + "</color></b>" +
                    HighlightVocabulary(text.Substring(end));
            }
            return HighlightVocabulary(text);
        }

        string HighlightVocabulary(string text)
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

        float LayoutReadingGroups(LearningReadingGroup[] groups)
        {
            if (groups == null || groups.Length == 0 || groups.Length > MaximumReadingGroups || string.IsNullOrEmpty(m_Overlay?.jp))
                return 0f;
            var sentence = new System.Text.StringBuilder(m_Overlay.jp.Length);
            foreach (var group in groups)
            {
                if (group == null || string.IsNullOrWhiteSpace(group.jp) || string.IsNullOrWhiteSpace(group.romaji))
                    return 0f;
                sentence.Append(group.jp);
            }
            if (!string.Equals(sentence.ToString(), m_Overlay.jp, StringComparison.Ordinal)) return 0f;

            const float gap = 16;
            const float width = Width - 2 * CaptionSidePadding;
            float japaneseHeight = JapaneseSize * 1.1f, kanaHeight = 0f, romajiHeight = GroupReadingSize * 1.1f;
            var widths = new float[groups.Length];
            var rowWidths = new float[2];
            int row = 0, secondRowAt = groups.Length, sentenceOffset = 0;
            for (int i = 0; i < groups.Length; i++)
            {
                if (i >= m_ReadingCells.Count) m_ReadingCells.Add(CreateReadingCell());
                var cell = m_ReadingCells[i];
                cell.japanese.text = Highlight(groups[i].jp, sentenceOffset);
                cell.kana.text = groups[i].jp == groups[i].kana ? "" : groups[i].kana ?? "";
                sentenceOffset += groups[i].jp.Length;
                cell.romaji.text = groups[i].romaji;
                widths[i] = Mathf.Ceil(Mathf.Max(cell.japanese.preferredWidth, cell.kana.preferredWidth, cell.romaji.preferredWidth)) + 2;
                if (widths[i] > width) return 0f;
                japaneseHeight = Mathf.Max(japaneseHeight, cell.japanese.preferredHeight);
                if (!string.IsNullOrEmpty(cell.kana.text)) kanaHeight = Mathf.Max(kanaHeight, cell.kana.preferredHeight);
                romajiHeight = Mathf.Max(romajiHeight, cell.romaji.preferredHeight);
                if (rowWidths[row] + gap + widths[i] > width && rowWidths[row] > 0)
                {
                    if (row == 1) return 0f;
                    row = 1;
                    secondRowAt = i;
                }
                rowWidths[row] += (rowWidths[row] == 0 ? 0 : gap) + widths[i];
            }
            float rowHeight = japaneseHeight + kanaHeight + romajiHeight;
            float height = (row + 1) * rowHeight;
            if (height + MinimumLineHeight(m_Translation) + 24 > m_AvailableHeight) return 0f;
            for (int i = 0; i < m_ReadingCells.Count; i++)
            {
                var cell = m_ReadingCells[i];
                cell.japanese.gameObject.SetActive(i < groups.Length);
                cell.kana.gameObject.SetActive(i < groups.Length && !string.IsNullOrEmpty(cell.kana.text));
                cell.romaji.gameObject.SetActive(i < groups.Length);
                cell.underline.gameObject.SetActive(false);
            }
            float x = -rowWidths[0] * 0.5f;
            sentenceOffset = 0;
            for (int i = 0; i < groups.Length; i++)
            {
                if (i == secondRowAt) x = -rowWidths[1] * 0.5f;
                float y = i < secondRowAt ? 0 : -rowHeight;
                var cell = m_ReadingCells[i];
                PositionReadingCell(cell.japanese.rectTransform, x, y, widths[i], japaneseHeight);
                PositionReadingCell(cell.kana.rectTransform, x, y - japaneseHeight, widths[i], kanaHeight);
                PositionReadingCell(cell.romaji.rectTransform, x, y - japaneseHeight - kanaHeight, widths[i], romajiHeight);
                int groupEnd = sentenceOffset + groups[i].jp.Length;
                if (m_ShowCorrection && m_ResultAttempt.correction.start >= sentenceOffset && m_ResultAttempt.correction.end <= groupEnd)
                {
                    float lineWidth = Mathf.Min(widths[i], cell.japanese.preferredWidth);
                    PositionReadingCell(cell.underline, x + (widths[i] - lineWidth) * 0.5f,
                        y - japaneseHeight + 5, lineWidth, 3);
                    cell.underline.gameObject.SetActive(true);
                }
                sentenceOffset = groupEnd;
                x += widths[i] + gap;
            }
            return height;
        }

        (Text japanese, Text kana, Text romaji, RectTransform underline) CreateReadingCell()
        {
            var japanese = MakeText("Word", m_Japanese.font, JapaneseSize, m_Japanese.color);
            japanese.supportRichText = true;
            var kana = MakeText("Word kana", m_Japanese.font, GroupReadingSize, m_Romaji.color);
            var romaji = MakeText("Word romaji", m_Romaji.font, GroupReadingSize, m_Romaji.color);
            japanese.transform.SetParent(m_ReadingPanel, false);
            kana.transform.SetParent(m_ReadingPanel, false);
            romaji.transform.SetParent(m_ReadingPanel, false);
            japanese.horizontalOverflow = kana.horizontalOverflow = romaji.horizontalOverflow = HorizontalWrapMode.Overflow;
            var underline = new GameObject("Correction underline", typeof(Image)).GetComponent<Image>();
            underline.transform.SetParent(m_ReadingPanel, false);
            underline.color = new Color(1f, 0.68f, 0.46f);
            underline.raycastTarget = false;
            underline.gameObject.SetActive(false);
            return (japanese, kana, romaji, underline.rectTransform);
        }

        static void PositionReadingCell(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0, 1);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(x, y);
        }

        static string EscapeRichText(string text) => text.Replace('<', '＜').Replace('>', '＞');

        static string Shorten(string text, int length) => text.Length <= length ? text : text.Substring(0, length - 1) + "…";

        void LayoutButtons()
        {
            int count = 0;
            foreach (var button in m_Buttons) if (button.gameObject.activeSelf) count++;
            int index = 0;
            foreach (var button in m_Buttons)
                if (button.gameObject.activeSelf)
                    ((RectTransform)button.transform).anchoredPosition =
                        new Vector2((index++ - (count - 1) * 0.5f) * ControlButtonSpacing, 110);
            bool hasReturn = m_Return.gameObject.activeSelf;
            var returnRect = (RectTransform)m_Return.transform;
            returnRect.anchoredPosition = new Vector2(0, 212);
            returnRect.sizeDelta = new Vector2(310, 126);
            var conversationRect = (RectTransform)m_Conversation.transform;
            conversationRect.sizeDelta = new Vector2(m_ConversationActive ? 340 : 206, 136);
            ((RectTransform)m_ControlHint.transform).anchoredPosition = new Vector2(0, 18);
        }

        void UpdateActionHints()
        {
            if (m_HoveredButton && (!m_HoveredButton.isActiveAndEnabled || !m_HoveredButton.interactable))
                m_HoveredButton = null;
            foreach (var button in m_Buttons) button.targetGraphic.color = button == m_HoveredButton ? HoverPurple : ActionPurple;
            m_Return.targetGraphic.color = m_Return == m_HoveredButton ? HoverPurple : ActionPurple;
            m_ControlHint.text = m_HoveredButton
                ? $"Pinch to select {(m_HoveredButton == m_Explore ? m_ExploreLabel.text : m_HoveredButton.name)}"
                : m_ExploreHint ?? "Aim the pointer at an action";
        }

        void FitLayout()
        {
            const float padding = 12;
            float budget = Mathf.Max(1, m_AvailableHeight - padding * 2 - m_ReadingHeight);
            float japanese = MeasureLine(m_Japanese);
            float romaji = MeasureLine(m_Romaji);
            float translation = MeasureLine(m_Translation);
            float clerk = MeasureLine(m_Clerk);
            float learner = MeasureLine(m_Learner);
            float kana = MeasureLine(m_Kana);
            float status = MeasureLine(m_Status);

            // The current utterance retains its Japanese, romaji and translation at
            // fixed type sizes. Optional readings and the previous reply yield first.
            if (japanese + romaji + translation + clerk + learner + kana + status > budget) kana = 0;
            if (japanese + romaji + translation + clerk + learner + status > budget && (japanese > 0 || m_ReadingHeight > 0)) learner = 0;
            if (japanese + romaji + translation + clerk + learner + status > budget)
                status = 0;
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
            m_ReadingPanel.gameObject.SetActive(m_ReadingHeight > 0);
            if (m_ReadingHeight > 0)
            {
                m_ReadingPanel.offsetMin = new Vector2(CaptionSidePadding, -y - m_ReadingHeight);
                m_ReadingPanel.offsetMax = new Vector2(-CaptionSidePadding, -y);
                y += m_ReadingHeight;
            }
            LayoutCaption(m_Translation, ref y, translation);
            LayoutCaption(m_Kana, ref y, kana);
            LayoutCaption(m_Status, ref y, status);
            m_Height = Mathf.Min(m_AvailableHeight, y + padding);
            m_Panel.sizeDelta = new Vector2(Width, m_Height);
            m_Backdrop.SetActive(y > padding);
        }

        void UpdateWaveform()
        {
            float now = Time.unscaledTime;
            if (!m_Waveform[0].gameObject.activeSelf || now < m_NextWaveformSample) return;
            m_NextWaveformSample = now + 0.05f;
            Array.Copy(m_ListeningHistory, 1, m_ListeningHistory, 0, m_ListeningHistory.Length - 1);
            m_ListeningHistory[m_ListeningHistory.Length - 1] = now - m_LastListeningSample <= 0.16f ? m_ListeningLevel : 0f;
            for (int i = 0; i < m_Waveform.Length; i++)
                m_Waveform[i].sizeDelta = new Vector2(8, 4 + 27 * m_ListeningHistory[i]);
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
            rt.offsetMin = new Vector2(CaptionSidePadding, -100);
            rt.offsetMax = new Vector2(-CaptionSidePadding, 0);
            // Noto's line metrics are taller than the built-in Latin font.
            return Mathf.Max(text.preferredHeight + 6, text.fontSize * 1.5f);
        }

        static void LayoutLine(Text text, ref float y, float height)
        {
            text.gameObject.SetActive(height > 0);
            if (height <= 0) return;
            var rt = (RectTransform)text.transform;
            rt.offsetMin = new Vector2(CaptionSidePadding, -y - height);
            rt.offsetMax = new Vector2(-CaptionSidePadding, -y);
            y += height;
        }

        void LateUpdate()
        {
            if (m_ControlPanel && m_ControlPanel.gameObject.activeInHierarchy) UpdateActionHints();
            if (!m_Panel) return;
            if (m_ShowCorrection != ShouldShowCorrection()) Refresh();
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
            // The subtitle band excludes navigation, results, and input status.
            float available = screenHeight * 0.38f / pixelScale;
            if (Mathf.Abs(available - m_AvailableHeight) > 0.5f)
            {
                m_AvailableHeight = available;
                Refresh();
            }
            Vector3 anchor = bottom + up * (screenHeight * CaptionBottom + m_Height * pixelScale * 0.5f);
            if (headCamera) anchor += (headCamera.transform.position - anchor).normalized * Mathf.Max(0.002f, screenGap);
            m_Panel.SetPositionAndRotation(anchor, rotation);
            m_Panel.localScale = Vector3.one * pixelScale;
            float canvasHeight = screenHeight / pixelScale;
            m_Hud.anchoredPosition = new Vector2(0, -CaptionBottom * canvasHeight);
            m_Hud.sizeDelta = new Vector2(Width, canvasHeight);
            bool showExploreMarker = m_ExploreAim.HasValue && m_ExploreActive && headCamera;
            m_ExploreMarker.gameObject.SetActive(showExploreMarker);
            if (showExploreMarker)
            {
                Vector3 hit = m_ExploreAim.Value;
                m_ExploreMarker.transform.position = hit + (headCamera.transform.position - hit).normalized * screenGap;
            }
            if (m_ResultBadge.gameObject.activeInHierarchy)
                m_ResultBadge.transform.parent.localScale = Vector3.one
                    * (1f + 0.09f * Mathf.Clamp01((m_ResultEmphasisUntil - Time.unscaledTime) / 0.24f));
            UpdateWaveform();
            if (headCamera)
            {
                var heading = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up).normalized;
                if (heading.sqrMagnitude < 0.01f) return;
                var upright = Quaternion.LookRotation(heading, Vector3.up);
                // Give the fallback rail a short, downward sightline from arm's reach.
                var pose = m_Tabletop ?? new Pose(
                    headCamera.transform.position + heading * 0.48f - Vector3.up * 0.18f,
                    upright * Quaternion.Euler(18, 0, 0));
                m_ControlPanel.SetPositionAndRotation(pose.position, pose.rotation);
                m_ControlPanel.localScale = Vector3.one * 0.00040f;
            }
        }
    }
}
