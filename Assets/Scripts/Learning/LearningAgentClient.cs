using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ImmerseTheBay.Learning.Voice;
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

        public CartesiaConversationClient conversation;
        public bool IsConversationActive => m_VoiceActive || m_VoiceConnecting || m_Reconciling;
        string m_RunId;
        string m_ActiveSceneId, m_PrefetchedSceneId;
        Task<byte[]> m_ScenePrefetch;
        LearningResponse m_LastResponse;
        LearningAttempt m_LastAttempt;
        string m_LastPublishedAttemptId;
        float m_AttemptExpires;
        string m_CompletionRunId;
        public LearningAttempt VisibleAttempt => m_LastAttempt;
        public LearningProgress Progress => m_LastResponse?.progress;
        public LearningRoute CurrentRoute => m_LastResponse?.route;
        int m_Revision, m_AudioTurn;
        double m_LastPromptAt = double.NegativeInfinity;
        bool m_VoiceActive, m_VoiceConnecting, m_Reconciling, m_ToolRunning, m_Cancelling;
        readonly HashSet<string> m_Claims = new();
        readonly HashSet<string> m_VoiceTools = new();
        readonly Queue<(VoiceToolCall call, int turn)> m_Tools = new();

        public int Beat { get; private set; }
        public bool IsBusy { get; private set; }
        public bool IsDone { get; private set; }
        public bool HasActiveLesson => m_HasLesson && !m_NeedsRestart;
        public string ActiveRunId => HasActiveLesson ? m_RunId : null;
        public bool IsListening => m_RecognitionPending;
        /// <summary>Clerk line still downloading or playing; Speak() would cut it off.</summary>
        public bool IsClerkSpeaking => m_ClerkAudioRequest != null || (m_ClerkSpeaker && m_ClerkSpeaker.isPlaying);
        public bool IsExamplePending => m_ExampleAudioRequest != null;
        public bool IsExamplePlaying => m_ExampleSpeaker && m_ExampleSpeaker.isPlaying;
        public bool IsFeedbackPlaying => Time.unscaledTime < m_FeedbackUntil;
        public bool PlayExamples { get; private set; } = true;
        public string Status { get; private set; } = "Join a lesson from the web, or begin a lesson.";

        readonly ConcurrentQueue<Action> m_Main = new();
        ISpeechToText m_Speech;
        Func<string> m_Debug;
        UnityWebRequest m_Request;
        UnityWebRequest m_CompletionRequest;
        UnityWebRequest m_ClerkAudioRequest;
        AudioSource m_ClerkSpeaker;
        AudioClip m_ClerkAudio;
        UnityWebRequest m_ExampleAudioRequest;
        AudioSource m_ExampleSpeaker, m_FeedbackSpeaker;
        AudioClip m_ExampleAudio, m_PassCue, m_RetryCue, m_CompletionCue;
        float m_FeedbackUntil;
        readonly UnityWebRequest[] m_CaptionRequests = new UnityWebRequest[2];
        readonly string[] m_CaptionText = new string[2];

        [Serializable] sealed class CaptionTranslation { public string english, romaji; }
        [Serializable] sealed class CompletionSummary
        {
            public string runId, lessonId;
            public int revision, targetsEncountered, targetsAttempted, targetsCompleted, scoredAttempts, averageTranscriptMatch;
            public bool completed;
        }
        string m_Session = "", m_RunLesson = "";
        bool m_HasLesson, m_NeedsRestart, m_RecognitionPending;
        bool m_ListeningVisible;
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
            if (!conversation) conversation = GetComponent<CartesiaConversationClient>();
            if (!conversation) conversation = gameObject.AddComponent<CartesiaConversationClient>();
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
            if (conversation) { conversation.ToolCall += OnVoiceTool; conversation.StateChanged += OnVoiceState; conversation.Transcript += OnVoiceTranscript; conversation.Interrupted += OnVoiceInterrupted; }
            if (overlay) { overlay.ConversationRequested += ToggleConversation; overlay.ReturnRequested += ReturnToMain; }
            m_Speech = speechProvider as ISpeechToText;
            SetStatus(Status);
        }

        void OnDisable()
        {
            if (conversation) { conversation.ToolCall -= OnVoiceTool; conversation.StateChanged -= OnVoiceState; conversation.Transcript -= OnVoiceTranscript; conversation.Interrupted -= OnVoiceInterrupted; }
            if (overlay) { overlay.ConversationRequested -= ToggleConversation; overlay.ReturnRequested -= ReturnToMain; }
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

        void OnDestroy()
        {
            if (m_PassCue) Destroy(m_PassCue);
            if (m_RetryCue) Destroy(m_RetryCue);
            if (m_CompletionCue) Destroy(m_CompletionCue);
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
            if (overlay)
            {
                var portal = m_Speech as PortalSpeechToText;
                bool capturing = m_RecognitionPending && (portal ? portal.IsCapturing : m_Speech?.IsListening == true);
                if (m_ListeningVisible != capturing) overlay.SetListening(m_ListeningVisible = capturing);
                if (capturing && portal) overlay.SetListeningLevel(portal.InputLevel);
            }
            if (m_LastAttempt != null && Time.unscaledTime >= m_AttemptExpires)
            {
                m_LastAttempt = null;
                UpdatePracticeLabel();
            }
            if (m_RecognitionPending && Time.frameCount > m_RecognitionStartFrame + 1 &&
                (!speechProvider || !speechProvider.isActiveAndEnabled || m_Speech == null || !m_Speech.IsListening))
            {
                CancelSpeech();
                SetStatus("Speech input stopped. Try Speak again.");
            }
            if (context && overlay)
            {
                context.overlayPanel = overlay.ControlsPanel;
                if (visko) context.viskoScreen = visko.screen;
            }
        }

        /// <summary>Explicitly start/restart the shared scene, including a joined web session.</summary>
        public void BeginLesson() => BeginLessonInternal(null);

        /// <summary>Use after phone photo capture; reset precedes upload so the start image is not lost.</summary>
        public void BeginLessonWithImage(byte[] jpg) => BeginLessonInternal(jpg);

        async void BeginLessonInternal(byte[] jpg)
        {
            if (!isActiveAndEnabled || IsBusy || m_RecognitionPending || IsConversationActive) return;
            if (!visko || !visko.IsConnected || string.IsNullOrEmpty(visko.SessionId))
            { SetStatus("Connect the glasses to Visko before beginning a lesson."); return; }
            if (visko.glassesResolution != "native")
            { SetStatus("Glasses learning requires native resolution."); return; }
            // Capture identity before any await. Update must not invalidate this new operation.
            m_Session = visko.SessionId;
            m_RunLesson = lessonId;
            int generation = ++m_Generation;
            m_LastResponse = null;
            m_LastAttempt = null;
            m_LastPublishedAttemptId = null;
            ClearCompletion();
            StopExampleAudio();
            StopClerkAudio();
            UpdatePracticeLabel();
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
                m_RunId = null; m_Revision = 0; m_Claims.Clear();
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

        public void SubmitIntent(string intent) => Submit("", context ? context.Capture() : null, intent);
        public void ReturnToMain() => Submit("", context ? context.Capture() : null, null, "return_to_main");
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
            if (IsClerkSpeaking || IsExamplePending || IsFeedbackPlaying) return;
            if (!CanSubmit()) return;
            if (m_Speech == null || !speechProvider || !speechProvider.isActiveAndEnabled)
            { SetStatus("Speech input is unavailable. Use Hint or Next."); return; }
            if (m_Speech.IsListening) { SetStatus("Finishing the previous speech request…"); return; }
            int generation = m_Generation;
            int speechGeneration = ++m_SpeechGeneration;
            m_RecognitionPending = true;
            m_RecognitionStartFrame = Time.frameCount;
            SubscribeSpeech(generation, speechGeneration);
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
                ShowDialogue(false, text);
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
            if (overlay)
            {
                m_ListeningVisible = false;
                overlay.SetListening(false);
                overlay.SetListeningLevel(0);
            }
        }

        bool CanSubmit(bool fromVoice = false)
        {
            if (!isActiveAndEnabled || IsBusy || m_RecognitionPending || m_Reconciling || (!fromVoice && IsConversationActive)) return false;
            if (IsDone) { SetStatus("Lesson complete."); return false; }
            if (m_NeedsRestart) { SetStatus("Lesson interrupted. Leave or begin again."); return false; }
            if (!m_HasLesson) { SetStatus("Begin a lesson or follow one from the web first."); return false; }
            if (!visko || !visko.IsConnected || visko.SessionId != m_Session)
            { SetStatus("Local lesson controls require connected glasses. The editor mirrors the web."); return false; }
            return true;
        }

        async void Submit(string transcript, LearningContextSnapshot snapshot, string intent = null, string action = null)
        {
            if (!CanSubmit()) return;
            int generation = m_Generation;
            SetBusy(true);
            try
            {
                await visko.ConfigReady;
                EnsureCurrent(generation);
                await ExecuteTurn(transcript, snapshot, generation, intent, action);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { FailTurn(generation, e); }
            finally { if (IsCurrent(generation)) SetBusy(false); }
        }

        async Task ExecuteTurn(string transcript, LearningContextSnapshot snapshot, int generation, string intent = null, string action = null)
        {
            SetStatus("Preparing the next scene…");
            if (m_Claims.Count >= 256) throw new InvalidOperationException("Lesson operation limit reached; restart safely.");
            string id = Guid.NewGuid().ToString("N");
            string body = LearningContract.TurnJson(m_Session, m_RunLesson, Beat, transcript, snapshot, id, m_RunId, m_Revision, intent, action);
            string json;
            try { json = await Post("/api/agent/turn", body, generation); }
            catch (TransportException) { EnsureCurrent(generation); json = await Post("/api/agent/turn", body, generation); }
            var response = LearningContract.ParseResponse(json);
            LearningContract.ValidateOperation(response, id, m_RunId, m_Revision);
            m_RunId = response.operation.runId; m_Revision = response.operation.revision;
            if (!m_Claims.Add(id)) throw new InvalidOperationException("Scene operation already attempted.");
            // Portal has committed scoring. Media acknowledgement only gates advancing the target.
            PublishAttempt(response.attempt, playCue: true);
            bool nextTarget = response.overlay != null &&
                (response.route?.nodeId != m_LastResponse?.route?.nodeId ||
                 response.overlay.jp != m_LastResponse?.overlay?.jp);
            if (nextTarget || response.overlay == null) StopExampleAudio();
            if (!IsConversationActive && response.speech?.role == "clerk")
            {
                ShowClerkDialogue(response.speech);
                PlayClerkAudio(response.speech, generation);
            }
            string sceneId = response.scene?.id;
            bool switchScene = !string.IsNullOrEmpty(m_ActiveSceneId) &&
                !string.IsNullOrEmpty(sceneId) && sceneId != m_ActiveSceneId;
            if (switchScene)
            {
                if (response.visko.All(command => command.name != "set_prompt"))
                    throw new InvalidOperationException("The new lesson scene has no video prompt.");
                byte[] image = m_PrefetchedSceneId == sceneId && m_ScenePrefetch != null
                    ? await m_ScenePrefetch : null;
                image ??= await visko.PrefetchLessonScene(sceneId);
                EnsureCurrent(generation);
                visko.PrepareScenePreview(image);
                await visko.CommandAsync("reset", "{}");
                EnsureCurrent(generation);
                await visko.SetImage(image);
                EnsureCurrent(generation);
            }
            foreach (var command in response.visko)
            {
                EnsureCurrent(generation);
                if (visko.glassesResolution != "native")
                    throw new InvalidOperationException("Glasses learning requires native resolution.");
                if (command.name == "set_prompt")
                {
                    // Same pacing as Web. Every wait slice rechecks Leave/session cancellation.
                    double remaining = 2.0 - (Time.realtimeSinceStartupAsDouble - m_LastPromptAt);
                    while (remaining > 0)
                    {
                        await Task.Delay(Math.Max(1, Math.Min(100, (int)Math.Ceiling(remaining * 1000))));
                        EnsureCurrent(generation);
                        remaining = 2.0 - (Time.realtimeSinceStartupAsDouble - m_LastPromptAt);
                    }
                    EnsureCurrent(generation);
                    m_LastPromptAt = Time.realtimeSinceStartupAsDouble;
                }
                await visko.CommandAsync(command.name, command.Arguments());
                EnsureCurrent(generation);
            }
            if (switchScene)
            {
                await visko.CommandAsync("start", "{}");
                EnsureCurrent(generation);
                double deadline = Time.realtimeSinceStartupAsDouble + 22;
                while (!visko.HasLiveFrame)
                {
                    if (Time.realtimeSinceStartupAsDouble >= deadline)
                        throw new TimeoutException("The next live scene did not arrive. Please retry.");
                    await Task.Yield();
                    EnsureCurrent(generation);
                }
            }
            m_ActiveSceneId = sceneId;
            if (m_PrefetchedSceneId != response.nextSceneId)
            {
                m_PrefetchedSceneId = response.nextSceneId;
                m_ScenePrefetch = string.IsNullOrEmpty(m_PrefetchedSceneId) ? null : PreloadScene(m_PrefetchedSceneId);
            }
            int nextBeat = Beat + (response.advance == "next_line" ? 1 : 0);
            // Do not publish success until ALL native acknowledgements have completed.
            await visko.SendLearningEvent(LearningContract.SyncJson(json, m_Session, m_RunLesson, nextBeat));
            EnsureCurrent(generation);
            Beat = nextBeat;
            IsDone = response.advance == "scene_done";
            Present(response);
            SetStatus(IsDone ? "Lesson complete." : "");
            if (IsDone) PresentCompletion(playCue: true);
            else if (nextTarget && !IsConversationActive && PlayExamples)
                PlayExample(response.overlay.jp, generation);
        }

        async Task<byte[]> PreloadScene(string sceneId)
        {
            try { return await visko.PrefetchLessonScene(sceneId); }
            catch (Exception) { return null; } // A cache miss is retried only when this scene is needed.
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
                if (response.operation == null || string.IsNullOrEmpty(response.operation.runId)) { m_NeedsRestart = true; return; }
                if (response.operation.runId == m_RunId && response.operation.revision <= m_Revision) return;
                if (IsConversationActive) { m_NeedsRestart = true; conversation.Disconnect(); return; }
                CancelSpeech();
                if (response.operation.runId != m_RunId)
                {
                    m_LastAttempt = null;
                    m_LastPublishedAttemptId = null;
                    ClearCompletion();
                    StopExampleAudio();
                }
                m_RunId = response.operation.runId; m_Revision = response.operation.revision;
                m_RunLesson = response.lessonId;
                Beat = response.beat; // Already advanced by the driving client.
                IsDone = response.advance == "scene_done";
                m_HasLesson = true;
                m_NeedsRestart = false;
                Present(response);
                SetStatus(IsDone ? "Lesson complete." : "");
                if (IsDone) PresentCompletion(playCue: false);
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
                if (request.result == UnityWebRequest.Result.ConnectionError && request.responseCode == 0)
                    throw new TransportException();
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
            m_ActiveSceneId = m_PrefetchedSceneId = null;
            m_ScenePrefetch = null;
            m_Cancelling = true;
            conversation?.Disconnect();
            m_VoiceActive = m_VoiceConnecting = m_Reconciling = false;
            m_Tools.Clear(); m_VoiceTools.Clear();
            m_LastResponse = null;
            m_LastAttempt = null;
            m_LastPublishedAttemptId = null;
            ClearCompletion();
            UpdatePracticeLabel();
            m_RunId = null; m_Revision = 0; m_Claims.Clear();
            m_Generation++;
            m_Cancelling = false;
            m_Request?.Abort();
            ClearDialogue();
            StopClerkAudio();
            StopExampleAudio();
            if (m_FeedbackSpeaker) m_FeedbackSpeaker.Stop();
            m_FeedbackUntil = 0;
            CancelSpeech();
            SetBusy(false);
        }

        sealed class TransportException : Exception { public TransportException() : base("Lesson connection interrupted.") { } }

        void StopClerkAudio()
        {
            m_ClerkAudioRequest?.Abort();
            m_ClerkAudioRequest = null;
            if (m_ClerkSpeaker) { m_ClerkSpeaker.Stop(); m_ClerkSpeaker.clip = null; }
            if (m_ClerkAudio) Destroy(m_ClerkAudio);
            m_ClerkAudio = null;
            if (overlay) overlay.SetClerkSpeaking(false);
        }

        async void PlayClerkAudio(LearningSpeech speech, int generation)
        {
            if (string.IsNullOrWhiteSpace(speech.text)) return;
            StopClerkAudio();
            try
            {
                string url = visko.config.portalUrl.TrimEnd('/') + "/api/agent/clerk-speech";
                using var request = new UnityWebRequest(url, "POST");
                request.SetRequestHeader("x-tenin-audio-layout", "stereo");
                request.downloadHandler = new DownloadHandlerAudioClip(url, AudioType.WAV);
                string body = "{\"sessionId\":" + LearningContract.Quote(m_Session)
                    + ",\"lessonId\":" + LearningContract.Quote(m_RunLesson)
                    + ",\"runId\":" + LearningContract.Quote(m_RunId)
                    + ",\"revision\":" + m_Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(visko.config.portalToken)) request.SetRequestHeader("portal_token", visko.config.portalToken);
                request.timeout = 12;
                m_ClerkAudioRequest = request;
                try
                {
                    var operation = request.SendWebRequest();
                    while (!operation.isDone) await Task.Yield();
                    if (!IsCurrent(generation) || m_ClerkAudioRequest != request || IsConversationActive) return;
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning("[Learning] Clerk speech could not be loaded.");
                        return;
                    }
                    m_ClerkAudio = DownloadHandlerAudioClip.GetContent(request);
                    if (!m_ClerkAudio) return;
                    while (IsCurrent(generation) && m_ClerkAudioRequest == request && Time.unscaledTime < m_FeedbackUntil)
                        await Task.Yield();
                    if (!IsCurrent(generation) || m_ClerkAudioRequest != request || IsConversationActive) return;
                    if (!m_ClerkSpeaker)
                    {
                        m_ClerkSpeaker = gameObject.AddComponent<AudioSource>();
                        m_ClerkSpeaker.playOnAwake = false;
                        m_ClerkSpeaker.spatialBlend = 0;
                    }
                    m_ClerkSpeaker.clip = m_ClerkAudio;
                    m_ClerkSpeaker.Play();
                    if (overlay) overlay.SetClerkSpeaking(true);
                    Debug.Log("[Learning] Clerk speech started");
                    while (IsCurrent(generation) && m_ClerkAudioRequest == request && m_ClerkSpeaker.isPlaying)
                        await Task.Yield();
                }
                finally
                {
                    if (m_ClerkAudioRequest == request)
                    {
                        m_ClerkAudioRequest = null;
                        if (overlay) overlay.SetClerkSpeaking(false);
                    }
                }
            }
            catch (Exception) { Debug.LogWarning("[Learning] Clerk speech unavailable."); }
        }

        public void SetPlayExamples(bool enabled)
        {
            if (PlayExamples == enabled) return;
            PlayExamples = enabled;
            if (!enabled) StopExampleAudio();
            else if (HasActiveLesson && !IsBusy && !IsDone && !IsListening && !IsConversationActive)
                PlayExample(m_LastResponse?.overlay?.jp, m_Generation);
        }

        void StopExampleAudio()
        {
            m_ExampleAudioRequest?.Abort();
            m_ExampleAudioRequest = null;
            if (m_ExampleSpeaker) { m_ExampleSpeaker.Stop(); m_ExampleSpeaker.clip = null; }
            if (m_ExampleAudio) Destroy(m_ExampleAudio);
            m_ExampleAudio = null;
            if (overlay) overlay.SetExampleSpeaking(false);
        }

        async void PlayExample(string japanese, int generation)
        {
            if (string.IsNullOrWhiteSpace(japanese)) return;
            string url = visko.config.portalUrl.TrimEnd('/') + "/api/agent/speak";
            using var request = new UnityWebRequest(url, "POST");
            request.SetRequestHeader("x-tenin-audio-layout", "stereo");
            try
            {
                request.downloadHandler = new DownloadHandlerAudioClip(url, AudioType.WAV);
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(
                    "{\"text\":" + LearningContract.Quote(japanese) + ",\"language\":\"ja\"}"));
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(visko.config.portalToken))
                    request.SetRequestHeader("portal_token", visko.config.portalToken);
                request.timeout = 4;
                m_ExampleAudioRequest = request;
                var operation = request.SendWebRequest();
                while (!operation.isDone) await Task.Yield();
                if (!IsCurrent(generation) || m_ExampleAudioRequest != request) return;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[Learning] Pronunciation example unavailable; continuing without it.");
                    return;
                }
                m_ExampleAudio = DownloadHandlerAudioClip.GetContent(request);
                if (!m_ExampleAudio) return;
                while (IsCurrent(generation) && m_ExampleAudioRequest == request && (IsClerkSpeaking || IsFeedbackPlaying))
                    await Task.Yield();
                if (!IsCurrent(generation) || m_ExampleAudioRequest != request || IsConversationActive || IsListening) return;
                if (!m_ExampleSpeaker)
                {
                    m_ExampleSpeaker = gameObject.AddComponent<AudioSource>();
                    m_ExampleSpeaker.playOnAwake = false;
                    m_ExampleSpeaker.spatialBlend = 0;
                }
                m_ExampleSpeaker.clip = m_ExampleAudio;
                m_ExampleSpeaker.Play();
                if (overlay) overlay.SetExampleSpeaking(true);
                while (IsCurrent(generation) && m_ExampleAudioRequest == request && m_ExampleSpeaker.isPlaying)
                    await Task.Yield();
            }
            catch (Exception) { Debug.LogWarning("[Learning] Pronunciation example unavailable; continuing without it."); }
            finally
            {
                if (m_ExampleAudioRequest == request) StopExampleAudio();
            }
        }

        void PlayAttemptCue(bool passed)
        {
            PlayFeedbackCue(passed
                ? (m_PassCue ??= BuildCue("Answer matched", 660, 880))
                : (m_RetryCue ??= BuildCue("Try again", 520, 390)));
        }

        void PlayFeedbackCue(AudioClip cue)
        {
            if (!m_FeedbackSpeaker)
            {
                m_FeedbackSpeaker = gameObject.AddComponent<AudioSource>();
                m_FeedbackSpeaker.playOnAwake = false;
                m_FeedbackSpeaker.spatialBlend = 0;
            }
            m_FeedbackSpeaker.Stop();
            m_FeedbackSpeaker.PlayOneShot(cue);
            m_FeedbackUntil = Time.unscaledTime + cue.length;
        }

        static AudioClip BuildCue(string name, params float[] tones)
        {
            const int rate = 24000;
            const int segment = 1800;
            int frames = segment * tones.Length;
            var samples = new float[frames * 2];
            for (int frame = 0; frame < frames; frame++)
            {
                int offset = frame % segment;
                float envelope = Mathf.Sin(Mathf.PI * offset / segment);
                float tone = tones[frame / segment];
                float sample = 0.15f * envelope * envelope * Mathf.Sin(2 * Mathf.PI * tone * offset / rate);
                samples[frame * 2] = samples[frame * 2 + 1] = sample;
            }
            var clip = AudioClip.Create(name, frames, 2, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        void ClearDialogue()
        {
            ShowDialogue(true, null);
            ShowDialogue(false, null);
        }

        void ShowClerkDialogue(LearningSpeech speech) =>
            ShowDialogue(true, speech?.text, speech?.english, speech?.romaji);

        async void ShowDialogue(bool clerk, string original, string english = null, string romaji = null)
        {
            if (!overlay) return;
            int slot = clerk ? 0 : 1;
            original = original?.Trim();
            if (m_CaptionText[slot] == original) return;
            m_CaptionText[slot] = original;
            m_CaptionRequests[slot]?.Abort();
            m_CaptionRequests[slot] = null;
            if (clerk) overlay.SetClerkCaption(original, english, romaji); else overlay.SetLearnerCaption(original);
            if (clerk && !string.IsNullOrWhiteSpace(english) && !string.IsNullOrWhiteSpace(romaji)) return;
            if (string.IsNullOrEmpty(original) || !visko || !isActiveAndEnabled) return;

            try
            {
                await visko.ConfigReady;
                if (!this || !isActiveAndEnabled || m_CaptionText[slot] != original) return;
                using var request = new UnityWebRequest(visko.config.portalUrl.TrimEnd('/') + "/api/agent/translate", "POST");
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(
                    "{\"text\":" + LearningContract.Quote(original.Substring(0, Math.Min(original.Length, 2000))) +
                    (clerk ? ",\"includeRomaji\":true}" : "}")));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(visko.config.portalToken)) request.SetRequestHeader("portal_token", visko.config.portalToken);
                request.timeout = 10;
                m_CaptionRequests[slot] = request;
                try
                {
                    var operation = request.SendWebRequest();
                    while (!operation.isDone) await Task.Yield();
                    if (!this || !overlay || m_CaptionRequests[slot] != request || request.result != UnityWebRequest.Result.Success) return;
                    var translation = JsonUtility.FromJson<CaptionTranslation>(request.downloadHandler.text);
                    if (translation == null) return;
                    if (clerk) overlay.SetClerkCaption(original,
                        string.IsNullOrWhiteSpace(english) ? translation.english : english,
                        string.IsNullOrWhiteSpace(romaji) ? translation.romaji : romaji);
                    else if (!string.IsNullOrWhiteSpace(translation.english)) overlay.SetLearnerCaption(original, translation.english);
                    Debug.Log("[Learning] English caption displayed for " + (clerk ? "clerk" : "learner"));
                }
                finally { if (m_CaptionRequests[slot] == request) m_CaptionRequests[slot] = null; }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Debug.LogWarning("[Learning] Dialogue translation unavailable."); }
        }

        void Present(LearningResponse response)
        {
            PublishAttempt(response.attempt, playCue: false);
            m_LastResponse = response;
            if (!overlay) return;
            overlay.Show(response.overlay, response.feedback, IsDone, response.focusWords);
            overlay.SetRoute(response.route?.kind == "detour", response.route?.label);
            UpdatePracticeLabel();
            if (!m_VoiceActive) ShowClerkDialogue(response.speech);
        }

        void PublishAttempt(LearningAttempt attempt, bool playCue)
        {
            if (attempt == null || string.IsNullOrEmpty(attempt.id) || m_LastPublishedAttemptId == attempt.id) return;
            m_LastPublishedAttemptId = attempt.id;
            m_LastAttempt = attempt;
            m_AttemptExpires = Time.unscaledTime + 6f;
            UpdatePracticeLabel();
            if (!playCue || attempt.outcome == "skip") return;
            try { PlayAttemptCue(attempt.outcome is "pass" or "assisted"); }
            catch (Exception) { Debug.LogWarning("[Learning] Sentence result audio cue unavailable."); }
        }

        void UpdatePracticeLabel()
        {
            if (!overlay) return;
            var progress = Progress;
            string main = progress == null ? "" : $"Main {progress.mainStep}/{progress.mainTotal}";
            var route = CurrentRoute;
            if (route?.kind == "detour") main += $" · Side trip {route.step}/{route.total}";
            string attempt = LearningContract.AttemptLabel(m_LastAttempt);
            string visibleTargetId = route?.kind == "detour" ? route.nodeId : $"main:{Beat}";
            overlay.SetPracticeResult(main, attempt, m_LastAttempt?.outcome, m_LastAttempt?.id,
                m_LastAttempt?.targetId == visibleTargetId ? m_LastAttempt : null);
        }

        string BindingJson() => "{\"sessionId\":" + LearningContract.Quote(m_Session) + ",\"lessonId\":" + LearningContract.Quote(m_RunLesson) + "}";

        void PresentCompletion(bool playCue)
        {
            if (!overlay || string.IsNullOrEmpty(m_RunId) || m_CompletionRunId == m_RunId) return;
            m_CompletionRunId = m_RunId;
            overlay.SetCompletionSummary("Loading session results…");
            ReadCompletionSummary(m_RunId, m_RunLesson, m_Revision, m_Generation);
            if (playCue) PlayCompletionCue(m_RunId, m_Generation);
        }

        bool IsCompletionCurrent(string runId, int generation) =>
            this && isActiveAndEnabled && IsDone && m_CompletionRunId == runId &&
            m_RunId == runId && m_Generation == generation && EffectiveSessionId() == m_Session;

        void ClearCompletion()
        {
            m_CompletionRequest?.Abort();
            m_CompletionRequest = null;
            m_CompletionRunId = null;
            if (overlay) overlay.SetCompletionSummary("");
        }

        async void ReadCompletionSummary(string runId, string completedLessonId, int revision, int generation)
        {
            try
            {
                await visko.ConfigReady;
                if (!IsCompletionCurrent(runId, generation)) return;
                using var request = new UnityWebRequest(visko.config.portalUrl.TrimEnd('/') + "/api/agent/summary", "POST");
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(BindingJson()));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(visko.config.portalToken))
                    request.SetRequestHeader("portal_token", visko.config.portalToken);
                request.timeout = 6;
                m_CompletionRequest = request;
                try
                {
                    var operation = request.SendWebRequest();
                    while (!operation.isDone) await Task.Yield();
                    if (!IsCompletionCurrent(runId, generation) || m_CompletionRequest != request) return;
                    if (request.result != UnityWebRequest.Result.Success)
                        throw new InvalidOperationException("Summary request did not succeed.");
                    var summary = JsonUtility.FromJson<CompletionSummary>(request.downloadHandler.text);
                    // The route looks up the current session/lesson. A reset must never substitute another run.
                    if (summary == null || !summary.completed || summary.runId != runId ||
                        summary.lessonId != completedLessonId || summary.revision != revision ||
                        summary.targetsEncountered < 0 || summary.targetsAttempted < 0 ||
                        summary.targetsAttempted > summary.targetsEncountered || summary.targetsCompleted < 0 ||
                        summary.targetsCompleted > summary.targetsAttempted || summary.scoredAttempts < summary.targetsAttempted ||
                        (summary.scoredAttempts > 0 && (summary.averageTranscriptMatch < 0 || summary.averageTranscriptMatch > 100)))
                        throw new FormatException("Summary did not match the completed run.");
                    string details = $"Attempted {summary.targetsAttempted}/{summary.targetsEncountered} · Matched {summary.targetsCompleted}";
                    if (summary.scoredAttempts > 0) details += $" · {summary.averageTranscriptMatch}% average match";
                    if (overlay) overlay.SetCompletionSummary(details);
                }
                finally { if (m_CompletionRequest == request) m_CompletionRequest = null; }
            }
            catch (Exception)
            {
                if (IsCompletionCurrent(runId, generation) && overlay)
                    overlay.SetCompletionSummary("Session results unavailable.");
            }
        }

        async void PlayCompletionCue(string runId, int generation)
        {
            try
            {
                while (IsCompletionCurrent(runId, generation) && (IsFeedbackPlaying || IsClerkSpeaking))
                    await Task.Yield();
                if (IsCompletionCurrent(runId, generation))
                    PlayFeedbackCue(m_CompletionCue ??= BuildCue("Lesson complete", 523, 659, 784));
            }
            catch (Exception) { Debug.LogWarning("[Learning] Completion audio cue unavailable."); }
        }

        async Task VerifySnapshot(int generation)
        {
            string json = await Post("/api/agent/snapshot", BindingJson(), generation);
            var state = json.Trim() == "null" ? null : JsonUtility.FromJson<LearningSnapshot>(json);
            if (state == null || state.runId != m_RunId || state.revision != m_Revision || state.beat != Beat)
                throw new InvalidOperationException("Lesson state changed; leave and restart. No video commands were replayed.");
        }
        public async void ToggleConversation()
        {
            if (m_VoiceActive || m_VoiceConnecting) { conversation.Disconnect(); return; }
            if (!CanSubmit()) return;
            StopClerkAudio();
            StopExampleAudio();
            CancelSpeech();
            int generation = m_Generation;
            m_VoiceConnecting = true; SetBusy(true);
            try
            {
                await visko.ConfigReady; EnsureCurrent(generation);
                float speechDeadline = Time.realtimeSinceStartup + 5f;
                while (m_Speech != null && m_Speech.IsListening)
                {
                    EnsureCurrent(generation);
                    if (!m_VoiceConnecting) return;
                    if (Time.realtimeSinceStartup > speechDeadline) throw new InvalidOperationException("Legacy speech microphone is still releasing. Retry conversation.");
                    await Task.Yield();
                }
                await VerifySnapshot(generation);
                string json = await Post("/api/voice/token", BindingJson(), generation);
                var token = JsonUtility.FromJson<LearningVoiceSession>(json);
                if (token == null || token.runId != m_RunId || token.revision != m_Revision || string.IsNullOrEmpty(token.token)) throw new InvalidOperationException("Voice session does not match this lesson.");
                if (!m_VoiceConnecting) return;
                m_VoiceTools.Clear(); m_Tools.Clear();
                m_Cancelling = true;
                try { conversation.Connect(token.url, token.token); } finally { m_Cancelling = false; }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { FailTurn(generation, e); conversation.Disconnect(); }
            finally { if (IsCurrent(generation)) { m_VoiceConnecting = false; SetBusy(false); } }
        }
        void OnVoiceTranscript(string role, string text)
        {
            if (!m_VoiceActive) return;
            if (role == "user")
            {
                SetStatus("");
                ShowDialogue(false, text);
            }
            else if (role == "agent" || role == "assistant" || role == "clerk") ShowDialogue(true, text);
        }
        void OnVoiceInterrupted() { m_AudioTurn++; }
        void OnVoiceState(string state)
        {
            bool wasActive = m_VoiceActive || m_VoiceConnecting;
            m_VoiceActive = state != "off";
            if (state == "off") m_VoiceConnecting = false;
            if (overlay) overlay.SetConversationState(m_VoiceActive, state == "speaking", conversation.Status);
            if (state == "off" && wasActive && !m_Cancelling && isActiveAndEnabled) ReconcileVoice();
        }
        async void ReconcileVoice()
        {
            if (m_Reconciling) return;
            m_Reconciling = true; if (overlay) overlay.SetControlsLocked(true);
            int generation = m_Generation;
            try
            {
                while (IsBusy) { EnsureCurrent(generation); await Task.Yield(); }
                await VerifySnapshot(generation);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { FailTurn(generation, e); }
            finally { if (IsCurrent(generation)) { m_Reconciling = false; if (overlay) overlay.SetControlsLocked(m_NeedsRestart); } }
        }
        void OnVoiceTool(VoiceToolCall call)
        {
            if (!m_VoiceActive || string.IsNullOrEmpty(call.tool_call_id) || !m_VoiceTools.Add(call.tool_call_id)) return;
            if (m_VoiceTools.Count > 256 || m_Tools.Count >= 8) { conversation.Disconnect(); SetStatus("Voice call limit reached. Reconnect."); return; }
            m_Tools.Enqueue((call, m_AudioTurn));
            if (!m_ToolRunning) DrainVoiceTools();
        }
        async void DrainVoiceTools()
        {
            m_ToolRunning = true;
            int generation = m_Generation;
            try
            {
                while (m_Tools.Count > 0 && m_VoiceActive && IsCurrent(generation))
                {
                    var queued = m_Tools.Dequeue(); var call = queued.call;
                    bool failed = false; string result;
                    try
                    {
                        if (queued.turn != m_AudioTurn || !CanSubmit(true)) throw new InvalidOperationException("Lesson action is unavailable or interrupted.");
                        string intent = null, transcript = "", action = null;
                        if (call.tool_name == "branch_scene" && !string.IsNullOrWhiteSpace(call.parameters?.intent)) intent = call.parameters.intent;
                        else if (call.tool_name == "rehearse_line" && !string.IsNullOrWhiteSpace(call.parameters?.transcript)) transcript = call.parameters.transcript;
                        else if (call.tool_name == "return_to_main") action = "return_to_main";
                        else throw new InvalidOperationException("Unsupported lesson tool.");
                        SetBusy(true);
                        await ExecuteTurn(transcript, context ? context.Capture() : null, generation, intent, action);
                        result = "{\"accepted\":true,\"beat\":" + Beat + ",\"overlay\":" + (m_LastResponse?.overlay == null ? "null" : JsonUtility.ToJson(m_LastResponse.overlay)) + ",\"route\":" + (m_LastResponse?.route == null ? "null" : JsonUtility.ToJson(m_LastResponse.route)) + ",\"complete\":" + (IsDone ? "true" : "false") + "}";
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e) { failed = true; result = "{\"accepted\":false,\"error\":" + LearningContract.Quote(e.Message) + "}"; if (IsBusy) FailTurn(generation, e); }
                    finally { if (IsCurrent(generation)) SetBusy(false); }
                    if (m_VoiceActive && IsCurrent(generation) && call.expects_response) conversation.SendToolResult(call.tool_call_id, result, failed);
                }
            }
            finally { m_ToolRunning = false; if (m_Tools.Count > 0 && m_VoiceActive) DrainVoiceTools(); }
        }

        void SetBusy(bool busy) { IsBusy = busy; if (overlay) { overlay.SetBusy(busy); overlay.SetControlsLocked(m_Reconciling || m_NeedsRestart); } }
        void SetStatus(string status) { Status = status ?? ""; if (overlay) overlay.SetStatus(Status); }
    }
}
