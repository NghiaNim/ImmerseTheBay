using System.Collections;
using System.Collections.Generic;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using ImmerseTheBay.Learning;
using ImmerseTheBay.Visko;
using Unity.XR.XREAL;
using UnityEngine;
using UnityEngine.UI;

namespace ImmerseTheBay.Learning
{
    [DefaultExecutionOrder(-20)]
    public sealed class DevLessonControls : MonoBehaviour
    {
        public ViskoSession video;
        public LearningAgentClient lesson;
        public Camera head;
        public ARPlaneManager planes;
        public ARRaycastManager raycasts;
        readonly List<ARRaycastHit> planeHits = new();
        string placementStatus = "";
        bool practiceVisible;
        bool starting;
        float connectDeadline;
        static readonly int SceneOpacity = Shader.PropertyToID("_SceneOpacity");
        MaterialPropertyBlock sceneProperties;
        float sceneOpacity = 1f;
        string pendingLessonId;
        string pendingStartImage;
        byte[] pendingImage;
        string presetLessonId;
        string presetStartImage;
        Text status;
        // One-trigger flow: the phone's Start in glasses starts the session, then each new line listens once.
        bool autoStartPending;
        float nextLaunchPoll;
        bool wasBusy;
        bool autoListenArmed;
        float autoListenAt;
        bool wasListening;
        int silentRetries;
        const int MaxSilentRetries = 3;

        IEnumerator Start()
        {
            presetLessonId = lesson.lessonId;
            presetStartImage = lesson.startImage;
            XREALVirtualController controller;
            while (!(controller = FindFirstObjectByType<XREALVirtualController>()))
                yield return null;

            var canvas = controller.GetComponentInParent<Canvas>() ??
                controller.GetComponentInChildren<Canvas>(true);
            if (!canvas) yield break;

            // The phone shows this XR activity for the whole lesson (the glasses need it in front),
            // so this panel is styled like the Tenin "Lesson running" screen.
            var panel = new GameObject("Lesson running", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            var rect = panel.GetComponent<RectTransform>();
            rect.SetParent(canvas.transform, false);
            rect.anchorMin = new Vector2(0f, 0.6f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            panel.GetComponent<Image>().color = TeninBg;
            var group = panel.GetComponent<VerticalLayoutGroup>();
            group.padding = new RectOffset(56, 56, 90, 40);
            group.spacing = 18;
            group.childControlHeight = true;
            group.childForceExpandHeight = false;
            Label(panel.transform, 30, TeninPrimary, "TENIN", FontStyle.Bold);
            heading = Label(panel.transform, 56, TeninInk, "Lesson running", FontStyle.Bold);
            title = Label(panel.transform, 34, TeninInk, "");
            status = Label(panel.transform, 28, TeninMuted, "");
            AddButton(panel.transform, "End lesson", EndLessonAndReturn, primary: true, height: 120);
            var row = new GameObject("Lesson tools", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(panel.transform, false);
            var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 16;
            rowLayout.childControlHeight = true;
            AddButton(row.transform, "Speak", lesson.Speak);
            AddButton(row.transform, "Talk", lesson.ToggleConversation);
            AddButton(row.transform, "Center video", CenterVideo);
            AddButton(row.transform, "Place controls", PlaceControls);
            var more = new GameObject("Session tools", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            more.transform.SetParent(panel.transform, false);
            var moreLayout = more.GetComponent<HorizontalLayoutGroup>();
            moreLayout.spacing = 16;
            moreLayout.childControlHeight = true;
            AddButton(more.transform, "Start again", StartLive);
            AddButton(more.transform, "Back to Tenin", OpenPhonePractice);
        }

        static readonly Color TeninBg = new Color32(0xFB, 0xFA, 0xFF, 0xFF);
        static readonly Color TeninInk = new Color32(0x1E, 0x1B, 0x2E, 0xFF);
        static readonly Color TeninMuted = new Color32(0x5E, 0x5A, 0x72, 0xFF);
        static readonly Color TeninPrimary = new Color32(0x7A, 0x62, 0xD6, 0xFF);
        static readonly Color TeninLine = new Color32(0xE4, 0xE0, 0xF2, 0xFF);
        static readonly Color TeninPrimaryDark = new Color32(0x4B, 0x3A, 0x9B, 0xFF);
        Text heading, title;
        string currentTitle = "";

        /// <summary>End lesson on the phone: stop the lesson and go back to Tenin, which shows the summary.</summary>
        void EndLessonAndReturn()
        {
            bool wasRunning = practiceVisible || starting;
            EndPractice();
            ShowPhone(lessonEnded: wasRunning);
        }

        void EndPractice()
        {
            starting = false;
            autoListenArmed = false;
            ClearPendingSession();
            lesson.LeaveLesson();
            SetPracticeVisible(false);
        }

        static bool ConsumePhoneRequest(string method)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using var phone = new AndroidJavaClass("com.immersethebay.phone.PhonePracticeActivity");
            return phone.CallStatic<bool>(method);
#else
            return false;
#endif
        }

        void UpdateOneTriggerFlow()
        {
            if (status && Time.unscaledTime >= nextLaunchPoll)
            {
                nextLaunchPoll = Time.unscaledTime + 0.5f;
                if (ConsumePhoneRequest("consumeEndRequest") && (practiceVisible || starting)) EndPractice();
                if (ConsumePhoneRequest("consumeLaunchRequest"))
                {
                    // A new choice on the phone replaces whatever is running.
                    if (practiceVisible || starting) EndPractice();
                    autoStartPending = true;
                }
            }
            if (autoStartPending && !practiceVisible && !starting && !lesson.IsBusy && !lesson.IsConversationActive)
            {
                autoStartPending = false;
                StartLive();
            }

            // Listen after every finished step, once the clerk's line has finished.
            if (wasBusy && !lesson.IsBusy && practiceVisible && !lesson.IsDone) { autoListenArmed = true; autoListenAt = 0; silentRetries = 0; }
            // Recognition that ended without starting a turn was silence: listen again a few times.
            if (wasListening && !lesson.IsListening && !lesson.IsBusy && practiceVisible && !lesson.IsDone
                && !lesson.IsConversationActive && silentRetries < MaxSilentRetries)
            { silentRetries++; autoListenArmed = true; autoListenAt = Time.unscaledTime + 1f; }
            wasBusy = lesson.IsBusy;
            wasListening = lesson.IsListening;
            if (!autoListenArmed) return;
            if (!practiceVisible || lesson.IsDone || lesson.IsConversationActive || lesson.IsListening) { autoListenArmed = false; return; }
            if (lesson.IsBusy || lesson.IsClerkSpeaking) { autoListenAt = 0; return; }
            if (autoListenAt == 0) { autoListenAt = Time.unscaledTime + 0.6f; return; }
            if (Time.unscaledTime < autoListenAt) return;
            autoListenArmed = false;
            lesson.Speak();
        }

        void CenterVideo()
        {
            var heading = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up).normalized;
            if (heading.sqrMagnitude < 0.01f) return;
            video.screen.transform.SetPositionAndRotation(
                head.transform.position + heading * 1.6f, Quaternion.LookRotation(heading, Vector3.up));
        }

        void LateUpdate()
        {
            if (!practiceVisible) return;

            CenterVideo();
            // Keep the real tabletop visible as the wearer transitions from the lesson to the controls.
            float lookDown = Mathf.InverseLerp(0.22f, 0.60f, -head.transform.forward.y);
            float target = 1f - Mathf.SmoothStep(0f, 1f, lookDown);
            sceneOpacity = Mathf.MoveTowards(sceneOpacity, target, Time.unscaledDeltaTime * 5f);
            sceneProperties ??= new MaterialPropertyBlock();
            video.screen.GetPropertyBlock(sceneProperties);
            sceneProperties.SetFloat(SceneOpacity, sceneOpacity);
            video.screen.SetPropertyBlock(sceneProperties);
        }

        void PlaceControls()
        {
            if (!practiceVisible || !planes || !raycasts) return;
            var view = head.transform;
            var gaze = new Ray(view.position, view.forward);
            var toward = Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized;
            if (toward.sqrMagnitude < 0.01f ||
                !raycasts.Raycast(gaze, planeHits, TrackableType.PlaneWithinPolygon))
            {
                placementStatus = "Look at the tabletop and try again.";
                return;
            }

            foreach (var hit in planeHits)
            {
                var plane = planes.GetPlane(hit.trackableId);
                float belowEyes = view.position.y - hit.pose.position.y;
                if (!plane || plane.trackingState != TrackingState.Tracking ||
                    plane.alignment != PlaneAlignment.HorizontalUp || hit.distance > 1.1f ||
                    belowEyes < 0.12f || belowEyes > 0.65f) continue;

                var rotation = Quaternion.LookRotation(toward, Vector3.up) * Quaternion.Euler(90, 0, 0);
                lesson.overlay.PlaceControls(new Pose(hit.pose.position + Vector3.up * 0.012f, rotation));
                placementStatus = "Controls placed.";
                Debug.Log("[Learning] Controls placed on a tracked horizontal plane");
                return;
            }
            placementStatus = "No reachable tabletop in view.";
        }

        void StartLive()
        {
            CenterVideo();
            if (starting || lesson.IsBusy || lesson.IsConversationActive) return;
            pendingLessonId = presetLessonId;
            pendingStartImage = presetStartImage;
            pendingImage = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            using var phone = new AndroidJavaClass("com.immersethebay.phone.PhonePracticeActivity");
            var selection = phone.CallStatic<string[]>("selectedSession");
            if (selection?.Length == 3)
            {
                pendingLessonId = selection[0];
                pendingStartImage = selection[1];
                currentTitle = selection[2].Contains(" — ") ? selection[2].Substring(selection[2].IndexOf(" — ") + 3) : selection[2];
                if (string.IsNullOrEmpty(pendingStartImage))
                    pendingImage = phone.CallStatic<byte[]>("pendingImage", pendingLessonId);
            }
#endif
            // Lessons saved on the Portal (written scenarios, replays) may have no start image; video starts from the prompt.
            bool stored = pendingLessonId != null && (pendingLessonId.StartsWith("gen-") || pendingLessonId.StartsWith("rec-"));
            if (string.IsNullOrEmpty(pendingStartImage) && pendingImage == null && !stored)
            {
                placementStatus = "Photo session expired. Choose another session on your phone.";
                ClearPendingSession();
                return;
            }
            SetPracticeVisible(true);
            if (video.IsConnected && !string.IsNullOrEmpty(video.SessionId)) BeginSelectedLesson();
            else
            {
                starting = true;
                connectDeadline = Time.unscaledTime + 45f;
                video.CreateSession();
            }
        }

        void BeginSelectedLesson()
        {
            lesson.lessonId = pendingLessonId;
            // Only bundled images can be loaded here; Portal-side images (replays of photo sessions) start from the prompt.
            lesson.startImage = pendingStartImage != null && pendingStartImage.StartsWith("lessons/") ? pendingStartImage : "";
            if (pendingImage != null) lesson.BeginLessonWithImage(pendingImage);
            else lesson.BeginLesson();
            ClearPendingSession();
        }

        void ClearPendingSession()
        {
            pendingLessonId = null;
            pendingStartImage = null;
            pendingImage = null;
        }

        static void OpenPhonePractice() => ShowPhone();

        /// <summary>Brings the existing Tenin screen (same task, same instance) back to the front.</summary>
        static void ShowPhone(bool lessonEnded = false)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const int ReorderToFront = 0x00020000; // Intent.FLAG_ACTIVITY_REORDER_TO_FRONT
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = new AndroidJavaObject("android.content.Intent");
            using var target = intent.Call<AndroidJavaObject>("setClassName",
                activity.Call<string>("getPackageName"), "com.immersethebay.phone.TeninActivity");
            using var flagged = intent.Call<AndroidJavaObject>("addFlags", ReorderToFront);
            using var extra = intent.Call<AndroidJavaObject>("putExtra", "lessonEnded", lessonEnded);
            activity.Call("startActivity", intent);
#endif
        }

        void Update()
        {
            UpdateOneTriggerFlow();
            if (starting && video.IsConnected && !string.IsNullOrEmpty(video.SessionId))
            {
                starting = false;
                BeginSelectedLesson();
            }
            else if (starting && (video.Status.StartsWith("error", System.StringComparison.OrdinalIgnoreCase) ||
                video.Status == "FAILED" || Time.unscaledTime >= connectDeadline))
            {
                starting = false;
                ClearPendingSession();
            }
            if (practiceVisible && !starting && video.Status == "DISCONNECTED")
                SetPracticeVisible(false);
            if (status)
            {
                bool live = practiceVisible && video.IsConnected;
                heading.text = practiceVisible ? (starting ? "Starting in your glasses…" : lesson.IsDone ? "Lesson complete" : "Lesson running") : "Ready when you are";
                title.text = currentTitle;
                status.text = (lesson.IsListening ? "Listening… say the line." : lesson.Status) +
                    (live || !practiceVisible ? "" : "  ·  " + video.Status) + (placementStatus.Length > 0 ? "  ·  " + placementStatus : "");
            }
        }

        void SetPracticeVisible(bool visible)
        {
            practiceVisible = visible;
            placementStatus = "";
            video.screen.enabled = visible;
        }

        static Text Label(Transform parent, int size, Color? color = null, string text = "", FontStyle style = FontStyle.Normal)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = size;
            label.fontStyle = style;
            label.alignment = TextAnchor.MiddleLeft;
            label.color = color ?? Color.white;
            label.text = text;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.raycastTarget = false;
            return label;
        }

        static void AddButton(Transform row, string caption, UnityEngine.Events.UnityAction action, bool primary = false, float height = 96)
        {
            var go = new GameObject(caption, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(row, false);
            go.GetComponent<LayoutElement>().minHeight = height;
            var image = go.GetComponent<Image>();
            image.color = primary ? TeninPrimary : TeninLine;
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(action);
            var label = Label(go.transform, primary ? 36 : 26, primary ? Color.white : TeninPrimaryDark, caption, primary ? FontStyle.Bold : FontStyle.Normal);
            label.alignment = TextAnchor.MiddleCenter;
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
        }
    }
}
