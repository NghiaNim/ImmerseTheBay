using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Tasks;
using ImmerseTheBay.Visko;
using UnityEngine;
using UnityEngine.Networking;

namespace ImmerseTheBay.Learning
{
    /// <summary>
    /// Serialized lesson turns over the portal. Mirrored learn events only render; they never execute
    /// Reactor commands. BeginLesson explicitly resets the shared scene; no session starts on enable.
    /// </summary>
    public sealed class LearningAgentClient : MonoBehaviour
    {
        public ViskoSession visko;
        public LearningContext context;
        public LearningOverlay overlay;
        [Tooltip("AndroidSpeechToText on device; EditorSpeechToText for manual transcript injection.")]
        public MonoBehaviour speechProvider;
        public string lessonId = "ja-konbini-001";
        [Tooltip("Optional StreamingAssets image. Uploaded and accepted before the first agent turn.")]
        public string startImage = "";

        public int Beat { get; private set; }
        public bool IsBusy { get; private set; }
        public bool IsDone { get; private set; }
        public string Status { get; private set; } = "Join a lesson from the web, or begin a lesson.";

        readonly ConcurrentQueue<Action> m_Main = new();
        ISpeechToText m_Speech;
        Func<string> m_Debug;
        UnityWebRequest m_Request;
        string m_Session = "", m_RunLesson = "";
        bool m_HasLesson, m_NeedsRestart, m_RecognitionPending;
        int m_Generation;
        int m_MirrorEpoch;
        int m_SpeechGeneration;
        int m_RecognitionStartFrame;
        Action<string> m_PartialHandler, m_FinalHandler, m_ErrorHandler;

        void Awake()
        {
            if (!visko) visko = FindAnyObjectByType<ViskoSession>();
            if (!context) context = GetComponent<LearningContext>();
            if (!overlay) overlay = GetComponent<LearningOverlay>();
            m_Debug = () => "<b>== LEARNING ==</b>\n" + Status +
                $"\nlesson {m_RunLesson} line {Beat + 1} busy={IsBusy} done={IsDone}" +
                $"\nspeech {(m_RecognitionPending ? "recognizing" : "idle")} restart={m_NeedsRestart}";
        }

        void OnEnable()
        {
            DebugRegistry.Register(m_Debug);
            if (visko) visko.OnLearnMessage += OnLearnMessage;
            if (context) context.Intent += OnIntent;
            if (overlay)
            {
                overlay.SpeakRequested += Speak;
                overlay.HintRequested += Hint;
                overlay.NextRequested += Next;
                if (visko) overlay.screen = visko.screen;
            }
            m_Speech = speechProvider as ISpeechToText;
            SetStatus(Status);
        }

        void OnDisable()
        {
            m_MirrorEpoch++;
            DebugRegistry.Unregister(m_Debug);
            if (visko) visko.OnLearnMessage -= OnLearnMessage;
            if (context) context.Intent -= OnIntent;
            if (overlay)
            {
                overlay.SpeakRequested -= Speak;
                overlay.HintRequested -= Hint;
                overlay.NextRequested -= Next;
            }
            if (IsBusy) m_NeedsRestart = true;
            m_HasLesson = false; // /sync does not replay missed learn events on re-enable.
            CancelWork();
            if (overlay) overlay.Show(null, null, false);
            SetStatus("Follow the web lesson or begin again.");
            // The provider component owns Dispose and its microphone restoration lifecycle.
            m_Speech = null;
        }

        void Update()
        {
            // Invalidate pending callbacks before draining them when the Reactor session changed.
            string session = EffectiveSessionId();
            if (session != m_Session)
            {
                CancelWork();
                m_Session = session;
                m_HasLesson = false;
                m_NeedsRestart = false;
                IsDone = false;
                Beat = 0;
                if (overlay) overlay.Show(null, null, false);
                SetStatus(string.IsNullOrEmpty(session) ? "Join a lesson from the web." : "Connected. Begin a lesson or follow the web.");
            }
            while (m_Main.TryDequeue(out var action)) action();
            if (m_RecognitionPending && Time.frameCount > m_RecognitionStartFrame + 1 &&
                (!speechProvider || !speechProvider.isActiveAndEnabled || m_Speech == null || !m_Speech.IsListening))
            {
                CancelSpeech();
                SetStatus("Speech input stopped. Try Speak again.");
            }
            if (context && overlay)
            {
                context.overlayPanel = overlay.Panel;
                if (visko) context.viskoScreen = visko.screen;
            }
        }

        /// <summary>Explicitly start/restart the shared scene, including a joined web session.</summary>
        public void BeginLesson() => BeginLessonInternal(null);

        /// <summary>Use after phone photo capture; reset precedes upload so the start image is not lost.</summary>
        public void BeginLessonWithImage(byte[] jpg) => BeginLessonInternal(jpg);

        async void BeginLessonInternal(byte[] jpg)
        {
            if (!isActiveAndEnabled || IsBusy || m_RecognitionPending) return;
            if (!visko || !visko.IsConnected || string.IsNullOrEmpty(visko.SessionId))
            { SetStatus("Connect the glasses to Visko before beginning a lesson."); return; }
            if (visko.glassesResolution != "native")
            { SetStatus("Glasses learning requires native resolution."); return; }
            // Capture identity before any await. Update must not invalidate this new operation.
            m_Session = visko.SessionId;
            m_RunLesson = lessonId;
            int generation = ++m_Generation;
            SetBusy(true);
            m_HasLesson = false;
            m_NeedsRestart = true;
            IsDone = false;
            Beat = 0;
            if (overlay) overlay.Show(null, null, false);
            try
            {
                await visko.ConfigReady;
                EnsureCurrent(generation);
                SetStatus("Preparing lesson…");
                await visko.CommandAsync("reset", "{}");
                EnsureCurrent(generation);
                await Post("/api/agent/reset", "{\"sessionId\":" + LearningContract.Quote(m_Session) +
                    ",\"lessonId\":" + LearningContract.Quote(m_RunLesson) + "}", generation);
                if (jpg != null && jpg.Length > 0)
                {
                    await visko.SetImage(jpg);
                    EnsureCurrent(generation);
                }
                else if (!string.IsNullOrWhiteSpace(startImage))
                {
                    await visko.SetImageFromStreamingAssets(startImage);
                    EnsureCurrent(generation);
                }
                await ExecuteTurn("", context ? context.Capture() : null, generation);
                m_HasLesson = true;
                m_NeedsRestart = false;
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { FailTurn(generation, e); }
            finally { if (IsCurrent(generation)) SetBusy(false); }
        }

        /// <summary>Leaves Reactor as well as the lesson. Bind the integration menu's Leave action here.</summary>
        public void LeaveLesson()
        {
            CancelWork();
            m_HasLesson = false;
            m_NeedsRestart = false;
            IsDone = false;
            Beat = 0;
            if (overlay) overlay.Show(null, null, false);
            if (visko) visko.Leave();
            SetStatus("Left lesson.");
        }

        public void Next() => Submit("", context ? context.Capture("pinch") : new LearningContextSnapshot { gesture = "pinch" });
        public void Hint() => Submit("", context ? context.Capture("long_pinch") : new LearningContextSnapshot { gesture = "long_pinch" });
        void OnIntent(LearningContextSnapshot snapshot) => Submit("", snapshot);

        public void Speak()
        {
            if (m_RecognitionPending)
            {
                // Stop requests a final result. E retains mic ownership until final/error.
                m_Speech?.StopListening();
                SetStatus("Finishing speech…");
                return;
            }
            if (!CanSubmit()) return;
            if (m_Speech == null || !speechProvider || !speechProvider.isActiveAndEnabled)
            { SetStatus("Speech input is unavailable. Use Hint or Next."); return; }
            if (m_Speech.IsListening) { SetStatus("Finishing the previous speech request…"); return; }
            int generation = m_Generation;
            int speechGeneration = ++m_SpeechGeneration;
            m_RecognitionPending = true;
            m_RecognitionStartFrame = Time.frameCount;
            SubscribeSpeech(generation, speechGeneration);
            if (overlay) overlay.SetListening(true);
            SetStatus(m_Speech is EditorSpeechToText ? "Editor: submit the test transcript on the speech component." : "Speak the Japanese sentence.");
            try { m_Speech.StartListening(); }
            catch (Exception e) { EndRecognition(); SetStatus("Speech input failed: " + e.Message); }
        }

        void SubscribeSpeech(int generation, int speechGeneration)
        {
            m_PartialHandler = text => m_Main.Enqueue(() =>
            {
                if (SpeechCurrent(generation, speechGeneration)) SetStatus(text ?? "");
            });
            m_FinalHandler = text => m_Main.Enqueue(() =>
            {
                if (!SpeechCurrent(generation, speechGeneration)) return;
                EndRecognition();
                if (string.IsNullOrWhiteSpace(text)) { SetStatus("No speech heard. Try Speak again."); return; }
                Submit(text, context ? context.Capture() : null);
            });
            m_ErrorHandler = error => m_Main.Enqueue(() =>
            {
                if (!SpeechCurrent(generation, speechGeneration)) return;
                EndRecognition();
                SetStatus("Speech input: " + error);
            });
            m_Speech.PartialResult += m_PartialHandler;
            m_Speech.FinalResult += m_FinalHandler;
            m_Speech.Error += m_ErrorHandler;
        }

        bool SpeechCurrent(int generation, int speechGeneration) =>
            IsCurrent(generation) && m_RecognitionPending && speechGeneration == m_SpeechGeneration;

        void EndRecognition()
        {
            m_RecognitionPending = false;
            m_SpeechGeneration++;
            if (m_Speech != null)
            {
                m_Speech.PartialResult -= m_PartialHandler;
                m_Speech.FinalResult -= m_FinalHandler;
                m_Speech.Error -= m_ErrorHandler;
            }
            m_PartialHandler = m_FinalHandler = m_ErrorHandler = null;
            if (overlay) overlay.SetListening(false);
        }

        bool CanSubmit()
        {
            if (!isActiveAndEnabled || IsBusy || m_RecognitionPending) return false;
            if (IsDone) { SetStatus("Lesson complete."); return false; }
            if (m_NeedsRestart) { SetStatus("Lesson interrupted. Leave or begin again."); return false; }
            if (!m_HasLesson) { SetStatus("Begin a lesson or follow one from the web first."); return false; }
            if (!visko || !visko.IsConnected || visko.SessionId != m_Session)
            { SetStatus("Local lesson controls require connected glasses. The editor mirrors the web."); return false; }
            return true;
        }

        async void Submit(string transcript, LearningContextSnapshot snapshot)
        {
            if (!CanSubmit()) return;
            int generation = m_Generation;
            SetBusy(true);
            try
            {
                await visko.ConfigReady;
                EnsureCurrent(generation);
                await ExecuteTurn(transcript, snapshot, generation);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { FailTurn(generation, e); }
            finally { if (IsCurrent(generation)) SetBusy(false); }
        }

        async Task ExecuteTurn(string transcript, LearningContextSnapshot snapshot, int generation)
        {
            SetStatus("Preparing the next scene…");
            string json = await Post("/api/agent/turn", LearningContract.TurnJson(m_Session, m_RunLesson, Beat, transcript, snapshot), generation);
            var response = LearningContract.ParseResponse(json);
            foreach (var command in response.visko)
            {
                EnsureCurrent(generation);
                if (visko.glassesResolution != "native")
                    throw new InvalidOperationException("Glasses learning requires native resolution.");
                await visko.CommandAsync(command.name, command.Arguments());
                EnsureCurrent(generation);
            }
            int nextBeat = Beat + (response.advance == "next_line" ? 1 : 0);
            // Do not publish success until ALL native acknowledgements have completed.
            await visko.SendLearningEvent(LearningContract.SyncJson(json, m_Session, m_RunLesson, nextBeat));
            EnsureCurrent(generation);
            Beat = nextBeat;
            IsDone = response.advance == "scene_done";
            if (overlay) overlay.Show(response.overlay, response.feedback, IsDone);
            SetStatus(IsDone ? "Lesson complete." : "");
        }

        void OnLearnMessage(string json)
        {
            // Visko delivers on Update, but keep this safe for alternate adapters too.
            int epoch = m_MirrorEpoch;
            m_Main.Enqueue(() => ApplyMirror(json, epoch));
        }

        void ApplyMirror(string json, int epoch)
        {
            // A session announcement and its first learn event can arrive in the same frame.
            // Local turn cancellation on session change must not discard that valid first event.
            if (!isActiveAndEnabled || epoch != m_MirrorEpoch || IsBusy) return;
            try
            {
                var response = LearningContract.ParseResponse(json, false);
                if (response.type != "learn" || string.IsNullOrEmpty(response.sessionId) ||
                    response.beat < 0 || response.beat > 5000 || string.IsNullOrEmpty(response.lessonId)) return;
                // Device matches the actual Reactor connection; editor matches the announced portal session.
                if (response.sessionId != EffectiveSessionId()) return;
                CancelSpeech();
                m_RunLesson = response.lessonId;
                Beat = response.beat; // Already advanced by the driving client.
                IsDone = response.advance == "scene_done";
                m_HasLesson = true;
                m_NeedsRestart = false;
                if (overlay) overlay.Show(response.overlay, response.feedback, IsDone);
                SetStatus(IsDone ? "Lesson complete." : "");
            }
            catch (Exception) { SetStatus("Received an invalid lesson update."); }
        }

        async Task<string> Post(string path, string body, int generation)
        {
            EnsureCurrent(generation);
            using var request = new UnityWebRequest(visko.config.portalUrl.TrimEnd('/') + path, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(visko.config.portalToken)) request.SetRequestHeader("portal_token", visko.config.portalToken);
            request.timeout = 30;
            m_Request = request;
            try
            {
                var operation = request.SendWebRequest();
                while (!operation.isDone) { EnsureCurrent(generation); await Task.Yield(); }
                EnsureCurrent(generation);
                if (request.result != UnityWebRequest.Result.Success)
                    throw new InvalidOperationException("Lesson service request failed (" + request.responseCode + ").");
                return request.downloadHandler.text;
            }
            finally { if (ReferenceEquals(m_Request, request)) m_Request = null; }
        }

        bool IsCurrent(int generation) => this && isActiveAndEnabled && generation == m_Generation &&
            visko && visko.SessionId == m_Session;
        void EnsureCurrent(int generation) { if (!IsCurrent(generation)) throw new OperationCanceledException(); }

        string EffectiveSessionId()
        {
            if (!visko) return "";
#if UNITY_EDITOR
            return string.IsNullOrEmpty(visko.SessionId) ? visko.PortalSessionId : visko.SessionId;
#else
            return visko.SessionId;
#endif
        }

        void FailTurn(int generation, Exception error)
        {
            if (!IsCurrent(generation)) return;
            // A server may already have accepted the turn. Blind retries can skip a line or restart video.
            m_NeedsRestart = true;
            SetStatus("Lesson interrupted. Leave or begin again. " + error.Message);
        }

        void CancelSpeech()
        {
            bool pending = m_RecognitionPending;
            EndRecognition();
            if (pending) m_Speech?.StopListening();
        }

        void OnApplicationPause(bool paused)
        {
            if (!paused) return;
            m_MirrorEpoch++;
            if (IsBusy) m_NeedsRestart = true;
            m_HasLesson = false;
            CancelWork();
        }

        void CancelWork()
        {
            m_Generation++;
            m_Request?.Abort();
            CancelSpeech();
            SetBusy(false);
        }

        void SetBusy(bool busy) { IsBusy = busy; if (overlay) overlay.SetBusy(busy); }
        void SetStatus(string status) { Status = status ?? ""; if (overlay) overlay.SetStatus(Status); }
    }
}
