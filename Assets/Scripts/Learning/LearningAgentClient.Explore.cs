using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using ImmerseTheBay.Tracking;
using UnityEngine;
using UnityEngine.Networking;

namespace ImmerseTheBay.Learning
{
    public sealed partial class LearningAgentClient
    {
        const int ExploreJpegLimit = 2 * 1024 * 1024;
        const string ExploreJpegPrefix = "data:image/jpeg;base64,";
        const float PointHoldSeconds = 0.8f;
        const float PointDrift = 0.075f;
        const float PointGraceSeconds = 0.17f;
        bool m_LocalExplore, m_PointNeedsRelease;
        float m_PointStarted, m_PointSeen, m_ExploreMessageUntil;
        int m_PointHand, m_ExploreEpoch;
        Vector2 m_PointAnchor;
        Vector3 m_LastAimHit;
        bool m_PointMissing;
        AudioClip m_SelectedCue;
        string m_ExploreMessage;
        UnityWebRequest m_ExploreRequest;
        byte[] m_ExploreReturnFrame;

        [Serializable] sealed class ExploreResult
        {
            public string runId, sceneId, frameId, @object, label, intent, visualPrompt, startImage, seedSource;
            public string menuChoice;
            public int revision;
            public float confidence;
        }

        sealed class ExplorationSelection
        {
            public byte[] StartFrame, EntryFrame;
            public string Prompt;
        }

        bool CanExplore => m_LocalExplore && HasActiveLesson && !IsDone && !IsBusy && !IsConversationActive &&
            m_LastResponse?.route?.kind != "detour" && !string.IsNullOrEmpty(m_RunId) && m_Revision > 0 &&
            visko && visko.IsConnected && visko.SessionId == m_Session && visko.CanExploreLiveFrame;

        void UpdateExplore()
        {
            bool available = CanExplore;
            if (!available && m_ExploreRequest != null) CancelExploreInput();
            if (Time.unscaledTime >= m_ExploreMessageUntil) m_ExploreMessage = null;
            Vector3? aim = null;
            var hands = HandInput.Instance;
            var left = hands ? hands.Left : default;
            var right = hands ? hands.Right : default;
            if (!left.pointing && !right.pointing && !left.pinching && !right.pinching &&
                Time.unscaledTime - m_PointSeen > PointGraceSeconds)
                m_PointNeedsRelease = false;

            bool canSelect = !IsClerkSpeaking && !IsExamplePending && !IsExamplePlaying && !IsFeedbackPlaying;
            if (available && m_ExploreRequest == null && !m_PointNeedsRelease &&
                overlay && !overlay.HoveringLessonAction && overlay.headCamera)
            {
                var view = overlay.headCamera.transform;
                if (TryAim(view, right, out var point, out var hit)) aim = TrackPoint(1, point, hit, right.pinchDown, canSelect);
                else if (TryAim(view, left, out point, out hit)) aim = TrackPoint(-1, point, hit, left.pinchDown, canSelect);
                else if (m_PointHand != 0 && Time.unscaledTime - m_PointSeen <= PointGraceSeconds)
                {
                    m_PointMissing = true;
                    aim = m_LastAimHit;
                }
            }
            if (!aim.HasValue) m_PointHand = 0;
            overlay?.SetExploreState(aim, aim.HasValue ? (canSelect ? "Hold or pinch to select" : "Aim at an object") :
                m_ExploreRequest != null ? "Looking at the selected object…" : m_ExploreMessage);
        }

        Vector3? TrackPoint(int hand, Vector2 point, Vector3 hit, bool pinch, bool canSelect)
        {
            float now = Time.unscaledTime;
            if (!canSelect || m_PointHand != hand || (m_PointAnchor - point).sqrMagnitude > PointDrift * PointDrift)
            {
                m_PointHand = hand;
                m_PointAnchor = point;
                m_PointStarted = now;
            }
            else if (m_PointMissing) m_PointStarted += now - m_PointSeen;
            m_PointMissing = false;
            m_PointSeen = now;
            m_LastAimHit = hit;
            if (!canSelect || (!pinch && now - m_PointStarted < PointHoldSeconds)) return hit;
            m_PointNeedsRelease = true;
            Debug.Log(pinch ? "[Learning] Scene point pinched" : "[Learning] Held scene point selected");
            ObserveSelection(point, pinch);
            return null;
        }

        bool TryAim(Transform view, HandState hand, out Vector2 point, out Vector3 hit)
        {
            point = default;
            hit = default;
            var origin = view.position;
            var direction = hand.indexTip - origin;
            return (hand.pointing || hand.pinchDown) && direction.sqrMagnitude >= 0.0225f &&
                Vector3.Dot(view.forward, direction.normalized) >= 0.88f &&
                visko.TryAimAtLiveFrame(new Ray(origin, direction), out point, out hit);
        }

        void CancelExploreInput()
        {
            m_PointHand = 0;
            m_PointMissing = false;
            m_ExploreMessage = null;
            m_ExploreEpoch++;
            var request = m_ExploreRequest;
            m_ExploreRequest = null;
            request?.Abort();
        }

        void ShowExploreMessage(string message)
        {
            m_ExploreMessage = message;
            m_ExploreMessageUntil = Time.unscaledTime + 3f;
        }

        async void ObserveSelection(Vector2 point, bool pinch)
        {
            int epoch = ++m_ExploreEpoch;
            int generation = m_Generation, revision = m_Revision;
            string run = m_RunId, scene = m_ActiveSceneId;
            bool Current() => epoch == m_ExploreEpoch && IsCurrent(generation) &&
                run == m_RunId && revision == m_Revision && scene == m_ActiveSceneId;
            try
            {
                byte[] entryFrame = visko.CaptureExploreFrame(out long id);
                if (entryFrame == null || entryFrame.Length == 0 || entryFrame.Length > ExploreJpegLimit)
                {
                    ShowExploreMessage("Wait for the live scene, then try again.");
                    return;
                }
                string frameId = id.ToString(CultureInfo.InvariantCulture);
                string body = "{\"sessionId\":" + LearningContract.Quote(m_Session)
                    + ",\"lessonId\":" + LearningContract.Quote(m_RunLesson)
                    + ",\"runId\":" + LearningContract.Quote(run)
                    + ",\"revision\":" + revision.ToString(CultureInfo.InvariantCulture)
                    + ",\"sceneId\":" + LearningContract.Quote(scene)
                    + ",\"frameId\":" + LearningContract.Quote(frameId)
                    + ",\"frame\":\"" + ExploreJpegPrefix + Convert.ToBase64String(entryFrame) + "\""
                    + ",\"point\":{\"x\":" + point.x.ToString("R", CultureInfo.InvariantCulture)
                    + ",\"y\":" + point.y.ToString("R", CultureInfo.InvariantCulture) + "},\"gesture\":\""
                    + (pinch ? "pinch" : "hold") + "\"}";

                using var request = new UnityWebRequest(visko.config.portalUrl.TrimEnd('/') + "/api/vision/explore", "POST");
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(visko.config.portalToken))
                    request.SetRequestHeader("portal_token", visko.config.portalToken);
                request.timeout = 42;
                request.redirectLimit = 0;
                m_ExploreRequest = request;
                ExploreResult result;
                try
                {
                    var operation = request.SendWebRequest();
                    const ulong maximumReplyBytes = 4 * 1024 * 1024;
                    while (!operation.isDone && Current() && request.downloadedBytes <= maximumReplyBytes)
                        await Task.Yield();
                    if (!operation.isDone) request.Abort();
                    if (!Current()) return;
                    if (request.result != UnityWebRequest.Result.Success || request.downloadedBytes > maximumReplyBytes)
                    {
                        ShowExploreMessage(request.responseCode == 429 ? "Explore is busy. Try again in a moment."
                            : "Could not explore the object. Continue your lesson.");
                        return;
                    }
                    result = JsonUtility.FromJson<ExploreResult>(request.downloadHandler.text);
                }
                finally { if (ReferenceEquals(m_ExploreRequest, request)) m_ExploreRequest = null; }

                if (!Current() || !CanExplore || result == null || result.frameId != frameId ||
                    result.runId != run || result.revision != revision ||
                    !string.Equals(result.sceneId ?? "", scene ?? "", StringComparison.Ordinal)) return;
                if (string.IsNullOrEmpty(result.@object))
                {
                    ShowExploreMessage("No object found. Aim and try again.");
                    return;
                }
                if (!(result.confidence is >= 0.8f and <= 1f)) return;
                bool menuSelection = scene == "cafe-menu" && result.@object == "menu_board" &&
                    result.menuChoice is "coffee" or "milk";
                var seed = menuSelection ? null : DecodeExploreSeed(result.startImage);
                if (!menuSelection && (seed == null ||
                    string.IsNullOrWhiteSpace(result.intent) || result.intent.Length > 300 ||
                    string.IsNullOrWhiteSpace(result.visualPrompt) || result.visualPrompt.Length > 1500)) return;

                // A valid selected object can interrupt listening. A miss leaves both mic and score as-is.
                CancelSpeech();
                if (!CanSubmit()) return;
                StopClerkAudio();
                StopExampleAudio();
                ConfirmExploreSelection(menuSelection ? (result.menuChoice == "milk" ? "Latte selected" : "Coffee selected") : "Selected");
                SetBusy(true);
                try
                {
                    if (menuSelection)
                        await ExecuteTurn("", new LearningContextSnapshot(), generation, action: "menu_select_" + result.menuChoice);
                    else
                    {
                        var snapshot = context ? context.Capture("point", result.@object)
                            : new LearningContextSnapshot { gesture = "point", pointedAt = result.@object };
                        await ExecuteTurn("", snapshot, generation, intent: result.intent,
                            exploration: new ExplorationSelection { StartFrame = seed, EntryFrame = entryFrame, Prompt = result.visualPrompt });
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { FailTurn(generation, error); }
                finally { if (IsCurrent(generation)) SetBusy(false); }
            }
            catch (Exception)
            {
                if (Current()) ShowExploreMessage("Could not explore the object. Continue your lesson.");
            }
        }

        void ConfirmExploreSelection(string message)
        {
            ShowExploreMessage("✓  " + message);
            PlayFeedbackCue(m_SelectedCue ??= BuildCue("Object selected", 600, 750));
        }

        static byte[] DecodeExploreSeed(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > ExploreJpegPrefix.Length + (ExploreJpegLimit + 2) / 3 * 4 ||
                !value.StartsWith(ExploreJpegPrefix, StringComparison.Ordinal)) return null;
            var bytes = Convert.FromBase64String(value.Substring(ExploreJpegPrefix.Length));
            return bytes.Length is >= 12 and <= ExploreJpegLimit && bytes[0] == 255 && bytes[1] == 216 &&
                bytes[bytes.Length - 2] == 255 && bytes[bytes.Length - 1] == 217 ? bytes : null;
        }
    }
}
