using ImmerseTheBay.Media;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace ImmerseTheBay.Content
{
    /// <summary>World-space video controls driven by XRI's UI input module and poke/ray interactors.</summary>
    public class VideoControlStrip : MonoBehaviour
    {
        const float Width = 760f;
        const float Height = 100f;
        SpatialMediaPlayer m_Player;
        RectTransform m_Panel;
        Canvas m_Canvas;
        Button m_PlayPause, m_Back, m_Forward, m_Mute;
        Text m_PlayLabel, m_MuteLabel;
        Vector3 m_ScreenScale;

        internal void Initialize(SpatialMediaPlayer player)
        {
            m_Player = player;
            m_Canvas = gameObject.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.WorldSpace;
            m_Canvas.worldCamera = Camera.main;
            m_Panel = (RectTransform)transform;
            m_Panel.sizeDelta = new Vector2(Width, Height);
            var raycaster = gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
            raycaster.checkFor3DOcclusion = true;

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            m_PlayPause = AddButton(0, "Play", font, out m_PlayLabel);
            m_Back = AddButton(1, "-10s", font, out _);
            m_Forward = AddButton(2, "+10s", font, out _);
            m_Mute = AddButton(3, "Mute", font, out m_MuteLabel);
            m_PlayPause.onClick.AddListener(TogglePlayback);
            m_Back.onClick.AddListener(() => Seek(-10));
            m_Forward.onClick.AddListener(() => Seek(10));
            m_Mute.onClick.AddListener(ToggleMute);
            UpdatePlacement();
            RefreshButtons();
        }

        Button AddButton(int index, string label, Font font, out Text text)
        {
            const float gap = 12f;
            float width = (Width - gap * 5) / 4;
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(m_Panel, false);
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(width, Height - gap * 2);
            rect.anchoredPosition = new Vector2(-Width / 2 + gap + width / 2 + index * (width + gap), 0);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.12f, 0.25f, 0.38f, 0.95f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(rect, false);
            var labelRect = (RectTransform)labelObject.transform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            text = labelObject.GetComponent<Text>();
            text.font = font;
            text.fontSize = 28;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = label;
            text.raycastTarget = false;
            return button;
        }

        void LateUpdate()
        {
            if (!m_Player || !m_Panel) return;
            if (!m_Canvas.worldCamera) m_Canvas.worldCamera = Camera.main;
            // SpatialMediaPlayer changes the quad's aspect ratio when preparation completes.
            if (m_ScreenScale != m_Player.transform.localScale)
                UpdatePlacement();
            RefreshButtons();
        }

        void UpdatePlacement()
        {
            m_ScreenScale = m_Player.transform.localScale;
            float pixelSize = Mathf.Max(0.01f, Mathf.Abs(m_ScreenScale.x)) / Width;
            m_Panel.localScale = Vector3.one * pixelSize;
            // Keep UI outside the grab collider and on the viewer-facing side of the quad.
            m_Panel.localPosition = m_Player.transform.localPosition +
                new Vector3(0, -Mathf.Abs(m_ScreenScale.y) / 2 - Height * pixelSize / 2 - 0.02f, -0.015f);
            m_Panel.localRotation = Quaternion.identity;
        }

        void RefreshButtons()
        {
            var video = m_Player ? m_Player.Video : null;
            bool ready = video && video.isPrepared;
            m_PlayPause.interactable = ready;
            m_Back.interactable = m_Forward.interactable = ready && video.canSetTime;
            m_Mute.interactable = ready && video.audioTrackCount > 0 &&
                (video.audioOutputMode == VideoAudioOutputMode.AudioSource || video.audioOutputMode == VideoAudioOutputMode.Direct);
            m_PlayLabel.text = ready && video.isPlaying ? "Pause" : "Play";
            m_MuteLabel.text = ready && IsMuted(video) ? "Unmute" : "Mute";
        }

        void TogglePlayback()
        {
            var video = m_Player ? m_Player.Video : null;
            if (!video || !video.isPrepared) return;
            if (video.isPlaying) video.Pause();
            else video.Play();
            RefreshButtons();
        }

        void Seek(double seconds)
        {
            var video = m_Player ? m_Player.Video : null;
            if (!video || !video.isPrepared || !video.canSetTime) return;
            double target = System.Math.Max(0, video.time + seconds);
            if (video.length > 0 && !double.IsInfinity(video.length) && !double.IsNaN(video.length))
                target = System.Math.Min(target, video.length);
            video.time = target;
        }

        static bool IsMuted(VideoPlayer video)
        {
            if (video.audioTrackCount == 0) return false;
            bool found = false;
            for (ushort track = 0; track < video.audioTrackCount; track++)
            {
                if (video.audioOutputMode == VideoAudioOutputMode.AudioSource)
                {
                    var source = video.GetTargetAudioSource(track);
                    if (!source) continue;
                    if (!source.mute) return false;
                }
                else if (video.audioOutputMode == VideoAudioOutputMode.Direct)
                {
                    if (!video.GetDirectAudioMute(track)) return false;
                }
                else return false;
                found = true;
            }
            return found;
        }

        void ToggleMute()
        {
            var video = m_Player ? m_Player.Video : null;
            if (!video || !video.isPrepared) return;
            bool mute = !IsMuted(video);
            for (ushort track = 0; track < video.audioTrackCount; track++)
            {
                if (video.audioOutputMode == VideoAudioOutputMode.AudioSource)
                {
                    var source = video.GetTargetAudioSource(track);
                    if (source) source.mute = mute;
                }
                else if (video.audioOutputMode == VideoAudioOutputMode.Direct)
                    video.SetDirectAudioMute(track, mute);
            }
            RefreshButtons();
        }
    }
}
