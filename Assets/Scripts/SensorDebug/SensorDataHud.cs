using System;
using ImmerseTheBay.Tracking;
using UnityEngine;
using UnityEngine.UI;
using Keyboard = UnityEngine.InputSystem.Keyboard;

namespace ImmerseTheBay.SensorDebug
{
    /// <summary>Head-following sensor pages. The UI is built at runtime; no scene wiring is required.</summary>
    public class SensorDataHud : MonoBehaviour
    {
        public SensorDataCollector collector;
        [Tooltip("Metres in front of the head.")] public float distance = 1.4f;
        [Tooltip("World metres per canvas pixel.")] public float pixelSize = 0.0007f;
        public float followSpeed = 3f;
        [Range(1, 30)] public float refreshHz = 10f;
        public int fontSize = 24;
        public bool followHead = true;
        [Tooltip("Maximum duration of a page-switch pinch, in seconds.")]
        public float quickPinchSeconds = 0.4f;

        static readonly string[] s_Pages = { "Overview", "Hands", "AR", "Devices", "Sensors", "Modules" };
        public string CurrentPage => s_Pages[m_Page];

        Text m_Header, m_Body;
        RectTransform m_Panel, m_Viewport, m_Content;
        ScrollRect m_Scroll;
        int m_Page;
        float m_NextRefresh;
        Transform m_Cam;
        bool m_PagePinch, m_PinchPrevious;
        float m_PinchStarted;

        void Awake()
        {
            if (!collector) collector = FindAnyObjectByType<SensorDataCollector>();
            BuildUi();
        }

        void OnDisable() => m_PagePinch = false;

        public void NextPage() => ChangePage((m_Page + 1) % s_Pages.Length);
        public void PrevPage() => ChangePage((m_Page + s_Pages.Length - 1) % s_Pages.Length);

        /// <summary>Selects a page by name (case insensitive); unknown names leave the selection unchanged.</summary>
        public void SetPage(string page)
        {
            for (int i = 0; i < s_Pages.Length; i++)
                if (string.Equals(page, s_Pages[i], StringComparison.OrdinalIgnoreCase)) { ChangePage(i); return; }
        }

        void ChangePage(int page)
        {
            m_Page = page;
            m_NextRefresh = 0;
            if (m_Content) m_Content.anchoredPosition = Vector2.zero;
            if (m_Scroll) m_Scroll.StopMovement();
        }

        void BuildUi()
        {
            var canvasGo = new GameObject("HudCanvas", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            m_Panel = (RectTransform)canvasGo.transform;
            m_Panel.sizeDelta = new Vector2(1700, 950);
            m_Panel.localScale = Vector3.one * pixelSize;

            var bg = new GameObject("Bg", typeof(Image));
            bg.transform.SetParent(m_Panel, false);
            Stretch((RectTransform)bg.transform, Vector2.zero, Vector2.zero);
            bg.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.1f, 0.6f);
            bg.GetComponent<Image>().raycastTarget = false;

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            // Older generated scenes serialize fontSize=15. Keep those scenes readable after upgrading.
            int size = Mathf.Max(22, fontSize);
            m_Header = MakeText("Header", m_Panel, font, size + 4);
            var headerRt = (RectTransform)m_Header.transform;
            headerRt.anchorMin = new Vector2(0, 1); headerRt.anchorMax = Vector2.one;
            headerRt.offsetMin = new Vector2(20, -60); headerRt.offsetMax = new Vector2(-20, -12);
            m_Header.horizontalOverflow = HorizontalWrapMode.Overflow;

            var viewportGo = new GameObject("PageViewport", typeof(RectTransform), typeof(RectMask2D), typeof(ScrollRect));
            viewportGo.transform.SetParent(m_Panel, false);
            m_Viewport = (RectTransform)viewportGo.transform;
            Stretch(m_Viewport, new Vector2(20, 80), new Vector2(-20, -65));
            m_Body = MakeText("PageBody", m_Viewport, font, size);
            m_Content = (RectTransform)m_Body.transform;
            m_Content.anchorMin = new Vector2(0, 1); m_Content.anchorMax = Vector2.one;
            m_Content.pivot = new Vector2(0.5f, 1);
            m_Content.sizeDelta = Vector2.zero;
            m_Content.anchoredPosition = Vector2.zero;
            m_Scroll = viewportGo.GetComponent<ScrollRect>();
            m_Scroll.viewport = m_Viewport; m_Scroll.content = m_Content;
            m_Scroll.horizontal = false; m_Scroll.movementType = ScrollRect.MovementType.Clamped;

            var footer = MakeText("Controls", m_Panel, font, 22);
            var footerRt = (RectTransform)footer.transform;
            footerRt.anchorMin = Vector2.zero; footerRt.anchorMax = new Vector2(0.75f, 0);
            footerRt.offsetMin = new Vector2(20, 10); footerRt.offsetMax = new Vector2(-20, 65);
            footer.text = "PgUp/PgDn/Tab: page | Up/Down: scroll | L: log | Quick pinch:";
            for (int i = 0; i < 2; i++)
            {
                var turn = MakeText(i == 0 ? "PreviousPageZone" : "NextPageZone", m_Panel, font, size);
                var turnRt = (RectTransform)turn.transform;
                turnRt.anchorMin = new Vector2(0.75f + i * 0.125f, 0);
                turnRt.anchorMax = new Vector2(0.875f + i * 0.125f, 0);
                turnRt.offsetMin = new Vector2(4, 0); turnRt.offsetMax = new Vector2(-4, 70);
                turn.alignment = TextAnchor.MiddleCenter;
                turn.text = i == 0 ? "< PREV" : "NEXT >";
            }
        }

        static void Stretch(RectTransform rt, Vector2 min, Vector2 max)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = min; rt.offsetMax = max;
        }

        static Text MakeText(string name, Transform parent, Font font, int size)
        {
            var go = new GameObject(name, typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = font; text.fontSize = size; text.supportRichText = true;
            text.color = new Color(0.75f, 1f, 0.85f); text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate; text.raycastTarget = false;
            return text;
        }

        void LateUpdate()
        {
            if (!m_Cam && Camera.main) m_Cam = Camera.main.transform;
            HandleInput();
            if (m_Cam && followHead)
            {
                var target = m_Cam.position + m_Cam.forward * distance;
                float k = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
                transform.position = Vector3.Lerp(transform.position, target, k);
                var look = Quaternion.LookRotation(transform.position - m_Cam.position, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, look, k);
            }

            if (Time.unscaledTime < m_NextRefresh) return;
            m_NextRefresh = Time.unscaledTime + 1f / Mathf.Max(1, refreshHz);
            if (collector) collector.Collect();
            m_Header.text = $"{CurrentPage}  {m_Page + 1}/{s_Pages.Length}  |  FPS {(collector ? collector.FramesPerSecond : 0):F1}";
            m_Body.text = !collector ? "No SensorDataCollector found." : m_Page switch
            {
                0 => collector.SectionSystem + "\n" + collector.SectionHead + "\n" + collector.SectionXreal,
                1 => collector.SectionHands,
                2 => collector.SectionAR,
                3 => collector.SectionDevices,
                4 => collector.SectionSensors,
                _ => collector.SectionExtras,
            };
            m_Content.sizeDelta = new Vector2(0, Mathf.Max(m_Viewport.rect.height, m_Body.preferredHeight + 4));
        }

        void HandleInput()
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.pageUpKey.wasPressedThisFrame) PrevPage();
                if (kb.pageDownKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame) NextPage();
                float scroll = (kb.upArrowKey.isPressed ? 1 : 0) - (kb.downArrowKey.isPressed ? 1 : 0);
                float overflow = m_Content.rect.height - m_Viewport.rect.height;
                if (overflow > 0)
                    m_Scroll.verticalNormalizedPosition = Mathf.Clamp01(m_Scroll.verticalNormalizedPosition + scroll * 400 * Time.unscaledDeltaTime / overflow);
            }

            var hands = HandInput.Instance;
            if (!hands || !hands.Right.tracked) { m_PagePinch = false; return; }
            var hand = hands.Right;
            // Latch eligibility at pinch DOWN. Moving a content pinch into the HUD never arms a page turn.
            if (hand.pinchDown)
            {
                m_PagePinch = InPageTurnZone(hand.pinchPoint);
                m_PinchPrevious = m_Panel.InverseTransformPoint(hand.pinchPoint).x < m_Panel.rect.width * 0.375f;
                m_PinchStarted = Time.unscaledTime;
            }
            if (!m_PagePinch) return;
            if (!InPageTurnZone(hand.pinchPoint) || Time.unscaledTime - m_PinchStarted > quickPinchSeconds)
                m_PagePinch = false;
            else if (hand.pinchUp)
            {
                m_PagePinch = false;
                if (m_PinchPrevious) PrevPage();
                else NextPage();
            }
        }

        bool InPageTurnZone(Vector3 point)
        {
            if (!m_Cam || !m_Panel) return false;
            var view = m_Cam.InverseTransformPoint(point);
            if (view.z <= 0 || view.x <= 0 || view.y >= 0) return false;
            var local = m_Panel.InverseTransformPoint(point);
            var rect = m_Panel.rect;
            // Distance is to the finite panel surface, measured in world metres (including canvas scale).
            var closest = m_Panel.TransformPoint(new Vector3(Mathf.Clamp(local.x, rect.xMin, rect.xMax),
                Mathf.Clamp(local.y, rect.yMin, rect.yMax), 0));
            return local.x > rect.width * 0.25f && local.x <= rect.xMax &&
                   local.y < rect.yMin + 70 && local.y >= rect.yMin &&
                   Vector3.Distance(point, closest) <= 0.25f;
        }
    }
}
