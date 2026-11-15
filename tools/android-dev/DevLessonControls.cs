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

            var panel = new GameObject("Live practice controls", typeof(RectTransform), typeof(VerticalLayoutGroup));
            var rect = panel.GetComponent<RectTransform>();
            rect.SetParent(canvas.transform, false);
            rect.anchorMin = new Vector2(0.08f, 0.81f);
            rect.anchorMax = new Vector2(0.92f, 0.94f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var group = panel.GetComponent<VerticalLayoutGroup>();
            group.spacing = 8;
            status = Label(panel.transform, 28);
            var row = new GameObject("Practice actions", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(panel.transform, false);
            row.GetComponent<HorizontalLayoutGroup>().spacing = 12;
            AddButton(row.transform, "Start session", StartLive);
            AddButton(row.transform, "Speak", lesson.Speak);
            AddButton(row.transform, "Talk", lesson.ToggleConversation);
            AddButton(row.transform, "Center video", CenterVideo);
            AddButton(row.transform, "Place controls", PlaceControls);
            var photoRow = new GameObject("Phone practice actions", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            photoRow.transform.SetParent(panel.transform, false);
            AddButton(photoRow.transform, "Choose session", OpenPhonePractice);
            AddButton(row.transform, "End", () =>
            {
                starting = false;
                ClearPendingSession();
                lesson.LeaveLesson();
                SetPracticeVisible(false);
            });
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
                if (string.IsNullOrEmpty(pendingStartImage))
                    pendingImage = phone.CallStatic<byte[]>("pendingImage", pendingLessonId);
            }
#endif
            if (string.IsNullOrEmpty(pendingStartImage) && pendingImage == null)
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
            lesson.startImage = pendingStartImage;
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

        static void OpenPhonePractice()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            using var intent = new AndroidJavaObject("android.content.Intent");
            using var target = intent.Call<AndroidJavaObject>("setClassName",
                activity.Call<string>("getPackageName"), "com.immersethebay.phone.PhonePracticeActivity");
            activity.Call("startActivity", intent);
#endif
        }

        void Update()
        {
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
            if (status) status.text = video.Status + "  ·  " + lesson.Status + "  " + placementStatus;
        }

        void SetPracticeVisible(bool visible)
        {
            practiceVisible = visible;
            placementStatus = "";
            video.screen.enabled = visible;
        }

        static Text Label(Transform parent, int size)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = size;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;
            return label;
        }

        static void AddButton(Transform row, string caption, UnityEngine.Events.UnityAction action)
        {
            var go = new GameObject(caption, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(row, false);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.12f, 0.24f, 0.29f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(action);
            var label = Label(go.transform, 30);
            label.text = caption;
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
        }
    }
}
