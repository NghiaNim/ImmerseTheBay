using UnityEngine;
using UnityEngine.UI;

namespace ImmerseTheBay.SensorDebug
{
    /// <summary>
    /// World-space, head-following panel that prints everything <see cref="SensorDataCollector"/> gathers.
    /// Builds its own UI at runtime, so it only needs to be dropped on an empty GameObject.
    /// </summary>
    public class SensorDataHud : MonoBehaviour
    {
        public SensorDataCollector collector;
        [Tooltip("Metres in front of the head.")] public float distance = 1.4f;
        [Tooltip("World metres per canvas pixel.")] public float pixelSize = 0.0007f;
        public float followSpeed = 3f;
        [Range(1, 30)] public float refreshHz = 10f;
        public int fontSize = 15;
        public bool followHead = true;

        Text[] m_Columns;
        float m_NextRefresh;
        Transform m_Cam;

        void Awake()
        {
            if (!collector) collector = FindAnyObjectByType<SensorDataCollector>();
            BuildUi();
        }

        void BuildUi()
        {
            var canvasGo = new GameObject("HudCanvas", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rt = (RectTransform)canvasGo.transform;
            rt.sizeDelta = new Vector2(1700, 950);
            rt.localScale = Vector3.one * pixelSize;

            var bg = new GameObject("Bg", typeof(Image));
            bg.transform.SetParent(rt, false);
            var bgRt = (RectTransform)bg.transform;
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one; bgRt.offsetMin = bgRt.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.1f, 0.6f);

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_Columns = new Text[3];
            for (int i = 0; i < 3; i++)
            {
                var go = new GameObject($"Col{i}", typeof(Text));
                go.transform.SetParent(rt, false);
                var crt = (RectTransform)go.transform;
                crt.anchorMin = new Vector2(i / 3f, 0); crt.anchorMax = new Vector2((i + 1) / 3f, 1);
                crt.offsetMin = new Vector2(12, 10); crt.offsetMax = new Vector2(-8, -10);
                var t = go.GetComponent<Text>();
                t.font = font; t.fontSize = fontSize; t.supportRichText = true; t.color = new Color(0.75f, 1f, 0.85f);
                t.alignment = TextAnchor.UpperLeft;
                t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
                t.raycastTarget = false;
                m_Columns[i] = t;
            }
        }

        void LateUpdate()
        {
            if (!m_Cam && Camera.main) m_Cam = Camera.main.transform;
            if (m_Cam && followHead)
            {
                var target = m_Cam.position + m_Cam.forward * distance;
                float k = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
                transform.position = Vector3.Lerp(transform.position, target, k);
                var look = Quaternion.LookRotation(transform.position - m_Cam.position, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, look, k);
            }

            if (!collector || Time.unscaledTime < m_NextRefresh) return;
            m_NextRefresh = Time.unscaledTime + 1f / refreshHz;
            collector.Collect();
            m_Columns[0].text = collector.SectionSystem + "\n" + collector.SectionHead + "\n" + collector.SectionExtras;
            m_Columns[1].text = collector.SectionHands + "\n" + collector.SectionAR;
            m_Columns[2].text = collector.SectionSensors + "\n" + collector.SectionDevices;
        }
    }
}
