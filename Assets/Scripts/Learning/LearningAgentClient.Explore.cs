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
        bool m_ExploreArmed, m_LocalExplore;
        float m_ExploreUntil, m_ExploreMessageUntil;
        int m_ExploreEpoch;
        string m_ExploreMessage;
        UnityWebRequest m_ExploreRequest;
        byte[] m_ExploreReturnFrame;

        [Serializable] sealed class ExploreResult
        {
            public string runId, sceneId, frameId, @object, label, intent, visualPrompt, startImage, seedSource;
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

        void ToggleExplore()
        {
            if (m_ExploreArmed || m_ExploreRequest != null) { CancelExploreInput(); return; }
            if (!CanExplore) return;
            m_ExploreArmed = true;
            m_ExploreUntil = Time.unscaledTime + 12f;
            m_ExploreMessage = null;
            overlay?.SetExploreState(true, true, null, "Aim at an object, then pinch");
        }

        void UpdateExplore()
        {
            bool available = CanExplore;
            if ((m_ExploreArmed || m_ExploreRequest != null) && !available)
                CancelExploreInput();
            if (m_ExploreArmed && Time.unscaledTime >= m_ExploreUntil)
                CancelExploreInput();
            if (Time.unscaledTime >= m_ExploreMessageUntil && m_ExploreRequest == null && !m_ExploreArmed)
                m_ExploreMessage = null;

            Vector3? aim = null;
            if (m_ExploreArmed && overlay && !overlay.HoveringLessonAction && overlay.headCamera && HandInput.Instance)
            {
                var origin = overlay.headCamera.transform.position;
                var left = HandInput.Instance.Left;
                var right = HandInput.Instance.Right;
                bool aimedLeft = TryAim(origin, left, out var pointLeft, out var hitLeft);
                bool aimedRight = TryAim(origin, right, out var pointRight, out var hitRight);
                if (aimedRight && right.pinchDown) ObserveSelection(pointRight);
                else if (aimedLeft && left.pinchDown) ObserveSelection(pointLeft);
                else if (aimedRight) aim = hitRight;
                else if (aimedLeft) aim = hitLeft;
            }
            if (overlay)
            {
                string hint = m_ExploreArmed ? (aim.HasValue ? "Pinch to explore this object" : "Aim at an object, then pinch")
                    : m_ExploreRequest != null ? "Looking at the selected object…" : m_ExploreMessage;
                overlay.SetExploreState(available, m_ExploreArmed || m_ExploreRequest != null, aim, hint);
            }
        }

        bool TryAim(Vector3 origin, HandState hand, out Vector2 point, out Vector3 hit)
        {
            point = default;
            hit = default;
            var direction = hand.indexTip - origin;
            return hand.tracked && direction.sqrMagnitude >= 0.0225f &&
                visko.TryAimAtLiveFrame(new Ray(origin, direction), out point, out hit);
        }

        void CancelExploreInput()
        {
            m_ExploreArmed = false;
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

        async void ObserveSelection(Vector2 point)
        {
            m_ExploreArmed = false;
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
                    + ",\"y\":" + point.y.ToString("R", CultureInfo.InvariantCulture) + "},\"gesture\":\"pinch\"}";

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
                var seed = DecodeExploreSeed(result.startImage);
                if (seed == null || !(result.confidence is >= 0.8f and <= 1f) ||
                    string.IsNullOrWhiteSpace(result.intent) || result.intent.Length > 300 ||
                    string.IsNullOrWhiteSpace(result.visualPrompt) || result.visualPrompt.Length > 1500) return;

                // A valid selected object can interrupt listening. A miss leaves both mic and score as-is.
                CancelSpeech();
                if (!CanSubmit()) return;
                StopClerkAudio();
                StopExampleAudio();
                SetBusy(true);
                try
                {
                    var snapshot = context ? context.Capture("point", result.@object)
                        : new LearningContextSnapshot { gesture = "point", pointedAt = result.@object };
                    await ExecuteTurn("", snapshot, generation, intent: result.intent,
                        exploration: new ExplorationSelection { StartFrame = seed, EntryFrame = entryFrame, Prompt = result.visualPrompt });
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
