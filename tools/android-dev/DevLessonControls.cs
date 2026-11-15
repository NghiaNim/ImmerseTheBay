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

            // Tenin "Lesson running" (Tenin_phone-hifi/7 · Lesson running.png). The phone shows this XR
            // activity for the whole lesson because the glasses need it in front, so it covers the stock controller.
            serif = Font.CreateDynamicFontFromOSFont(new[] { "Noto Serif", "Noto Serif CJK JP", "serif" }, 48);
            var panel = new GameObject("Lesson running", typeof(RectTransform), typeof(Image));
            var rect = panel.GetComponent<RectTransform>();
            rect.SetParent(canvas.transform, false);
            rect.SetAsLastSibling();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            panel.GetComponent<Image>().color = TeninPrimary; // opaque: also blocks touches to the stock controller
            var t = panel.transform;

            Place(Label(t, 66, Color.white, "Tenin", font: serif), 0.06f, 0.905f, 0.5f, 0.965f);
            var pill = Box(t, new Color(1f, 1f, 1f, 0.16f), 0.52f, 0.91f, 0.94f, 0.955f, rounded: true);
            Place(Label(pill.transform, 32, Color.white, "●  Glasses connected", TextAnchor.MiddleCenter, font: serif), 0f, 0f, 1f, 1f);

            // The big circle is the microphone: it listens on its own after each line, and can be tapped any time.
            speakRing = Box(t, new Color(1f, 1f, 1f, 0.18f), 0.5f, 0.66f, 0.5f, 0.66f, rounded: true).GetComponent<RectTransform>();
            speakRing.sizeDelta = new Vector2(640, 640);
            speakButton = RoundButton(t, "Tap to\nspeak", lesson.Speak, 520, 0.5f, 0.66f);
            speakLabel = speakButton.GetComponentInChildren<Text>();

            heading = Place(Label(t, 74, Color.white, "Lesson running\nin your glasses", TextAnchor.MiddleCenter, font: serif), 0.05f, 0.415f, 0.95f, 0.51f);
            status = Place(Label(t, 38, new Color(1f, 1f, 1f, 0.92f), "", TextAnchor.MiddleCenter, font: serif), 0.08f, 0.345f, 0.92f, 0.41f);

            title = Place(Label(t, 36, Color.white, "", font: serif), 0.06f, 0.27f, 0.62f, 0.305f);
            step = Place(Label(t, 36, Color.white, "", TextAnchor.MiddleRight, font: serif), 0.62f, 0.27f, 0.94f, 0.305f);
            Box(t, new Color(1f, 1f, 1f, 0.25f), 0.06f, 0.255f, 0.94f, 0.262f, rounded: true);
            progressFill = Box(t, TeninYellow, 0.06f, 0.255f, 0.06f, 0.262f, rounded: true).GetComponent<RectTransform>();

            Place(Label(t, 32, new Color(1f, 1f, 1f, 0.85f), "You can end the lesson anytime.", TextAnchor.MiddleCenter, font: serif), 0.06f, 0.115f, 0.94f, 0.15f);
            var end = Box(t, Color.white, 0.06f, 0.04f, 0.94f, 0.105f, rounded: true);
            var endButton = end.AddComponent<Button>();
            endButton.targetGraphic = end.GetComponent<Image>();
            endButton.onClick.AddListener(EndLessonAndReturn);
            endLabel = Place(Label(end.transform, 44, TeninPrimaryDark, "■  End lesson", TextAnchor.MiddleCenter, FontStyle.Bold, serif), 0f, 0f, 1f, 1f);
        }

        Font serif;
        Button speakButton;
        Text speakLabel, step, endLabel;
        RectTransform speakRing, progressFill;
        static readonly Color TeninYellow = new Color32(0xFF, 0xE2, 0x99, 0xFF);

        static T Place<T>(T component, float x0, float y0, float x1, float y1) where T : Component
        {
            var rect = component.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(x0, y0);
            rect.anchorMax = new Vector2(x1, y1);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            return component;
        }

        static GameObject Box(Transform parent, Color color, float x0, float y0, float x1, float y1, bool rounded = false)
        {
            var go = new GameObject("Box", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            if (rounded) { image.sprite = Pill(); image.type = Image.Type.Sliced; image.pixelsPerUnitMultiplier = 0.5f; }
            Place(go.GetComponent<RectTransform>(), x0, y0, x1, y1);
            return go;
        }

        /// <summary>Large round button centred at an anchor point.</summary>
        static Button RoundButton(Transform parent, string caption, UnityEngine.Events.UnityAction action, float size, float x, float y)
        {
            var go = new GameObject("Speak", typeof(RectTransform), typeof(Image), typeof(Button));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(x, y);
            rect.sizeDelta = new Vector2(size, size);
            var image = go.GetComponent<Image>();
            image.sprite = Circle();
            image.color = new Color(1f, 1f, 1f, 0.22f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(action);
            Place(Label(go.transform, 58, Color.white, caption, TextAnchor.MiddleCenter, FontStyle.Bold), 0f, 0f, 1f, 1f);
            return button;
        }

        static Sprite circle, pill;
        /// <summary>White anti-aliased disc, tinted by the Image colour.</summary>
        static Sprite Circle() => circle ??= Disc(256, 0);
        /// <summary>Disc with 9-slice borders, so it stretches into a pill or rounded rectangle.</summary>
        static Sprite Pill() => pill ??= Disc(128, 63);

        static Sprite Disc(int n, int border)
        {
            var pixels = new Color32[n * n];
            float r = n / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                    pixels[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(r - d) * 255));
                }
            var texture = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        }

        /// <summary>Speak circle animation: pulses while listening, breathes gently while waiting.</summary>
        void AnimateSpeak()
        {
            if (!speakButton) return;
            float time = Time.unscaledTime;
            bool listening = lesson.IsListening, waiting = lesson.IsBusy || lesson.IsClerkSpeaking;
            float scale = listening ? 1f + 0.07f * Mathf.Sin(time * 6f) : waiting ? 0.94f : 1f + 0.02f * Mathf.Sin(time * 2f);
            speakButton.transform.localScale = Vector3.one * scale;
            speakRing.localScale = Vector3.one * (listening ? 1f + 0.12f * (0.5f + 0.5f * Mathf.Sin(time * 6f)) : 1f);
            speakRing.GetComponent<Image>().color = new Color(1f, 1f, 1f, listening ? 0.28f : 0.12f);
            speakButton.GetComponent<Image>().color = new Color(1f, 1f, 1f, listening ? 0.38f : waiting ? 0.12f : 0.22f);
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
            if (wasBusy && !lesson.IsBusy && practiceVisible && !lesson.IsDone)
            { autoListenArmed = true; autoListenAt = 0; silentRetries = 0; Debug.Log("[Flow] step finished; auto-listen armed"); }
            // Recognition that ended without starting a turn was silence: listen again a few times.
            if (wasListening && !lesson.IsListening && !lesson.IsBusy && practiceVisible && !lesson.IsDone
                && !lesson.IsConversationActive && silentRetries < MaxSilentRetries)
            { silentRetries++; autoListenArmed = true; autoListenAt = Time.unscaledTime + 1f; Debug.Log("[Flow] no speech; listening again " + silentRetries); }
            wasBusy = lesson.IsBusy;
            wasListening = lesson.IsListening;
            if (!autoListenArmed) return;
            if (!practiceVisible || lesson.IsDone || lesson.IsConversationActive || lesson.IsListening)
            { autoListenArmed = false; Debug.Log($"[Flow] auto-listen skipped visible={practiceVisible} done={lesson.IsDone} talk={lesson.IsConversationActive} listening={lesson.IsListening}"); return; }
            if (lesson.IsBusy || lesson.IsClerkSpeaking) { autoListenAt = 0; return; }
            if (autoListenAt == 0) { autoListenAt = Time.unscaledTime + 0.6f; return; }
            if (Time.unscaledTime < autoListenAt) return;
            autoListenArmed = false;
            Debug.Log("[Flow] auto-listen: Speak() status=" + lesson.Status);
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

        int totalLines;

        /// <summary>Line count for the progress bar: every authored line has exactly one "cue" (detours are extra).</summary>
        IEnumerator FetchLineCount(string lessonId)
        {
            totalLines = 0;
            var url = video.config.portalUrl.TrimEnd('/') + "/api/agent/lessons/" + UnityEngine.Networking.UnityWebRequest.EscapeURL(lessonId);
            using var request = UnityEngine.Networking.UnityWebRequest.Get(url);
            if (!string.IsNullOrEmpty(video.config.portalToken)) request.SetRequestHeader("portal_token", video.config.portalToken);
            request.timeout = 10;
            yield return request.SendWebRequest();
            if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success || lesson.lessonId != lessonId) yield break;
            var json = request.downloadHandler.text;
            int count = 0;
            for (int at = json.IndexOf("\"cue\":"); at >= 0; at = json.IndexOf("\"cue\":", at + 1)) count++;
            totalLines = count;
        }

        void BeginSelectedLesson()
        {
            lesson.lessonId = pendingLessonId;
            StartCoroutine(FetchLineCount(pendingLessonId));
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
                bool waiting = lesson.IsBusy || lesson.IsClerkSpeaking;
                heading.text = !practiceVisible ? "Ready when\nyou are" : starting ? "Starting in\nyour glasses…" : lesson.IsDone ? "Lesson\ncomplete" : "Lesson running\nin your glasses";
                status.text = !practiceVisible ? "Choose a lesson on your phone." : starting ? "Getting the scene ready…"
                    : lesson.IsDone ? "Well done. End the lesson to see your summary."
                    : lesson.IsListening ? "Listening… say the line." : waiting ? "Listen to the other person…" : "Say the line, or tap the circle.";
                speakLabel.text = lesson.IsListening ? "Listening…" : waiting ? "…" : "Tap to\nspeak";
                speakButton.interactable = practiceVisible && !lesson.IsBusy && !lesson.IsDone;
                speakButton.gameObject.SetActive(practiceVisible);
                speakRing.gameObject.SetActive(practiceVisible);
                title.text = practiceVisible ? currentTitle : "";
                int done = Mathf.Min(lesson.Beat + (lesson.IsDone ? 1 : 0), Mathf.Max(totalLines, 1));
                step.text = !practiceVisible || starting ? "" : totalLines > 0 ? $"Step {Mathf.Min(lesson.Beat + 1, totalLines)} of {totalLines}" : $"Step {lesson.Beat + 1}";
                progressFill.anchorMax = new Vector2(0.06f + 0.88f * (practiceVisible && totalLines > 0 ? (float)done / totalLines : 0f), progressFill.anchorMax.y);
                endLabel.text = practiceVisible ? "■  End lesson" : "Back to Tenin";
                AnimateSpeak();
            }
        }

        void SetPracticeVisible(bool visible)
        {
            practiceVisible = visible;
            placementStatus = "";
            video.screen.enabled = visible;
        }

        static Text Label(Transform parent, int size, Color? color = null, string text = "", TextAnchor align = TextAnchor.MiddleLeft,
            FontStyle style = FontStyle.Normal, Font font = null)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<Text>();
            label.font = font ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.fontSize = size;
            label.fontStyle = style;
            label.alignment = align;
            label.color = color ?? Color.white;
            label.text = text;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.raycastTarget = false;
            return label;
        }

    }
}
