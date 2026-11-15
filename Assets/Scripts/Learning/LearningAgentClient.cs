using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        LearningResponse m_LastResponse;
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
        public string Status { get; private set; } = "Join a lesson from the web, or begin a lesson.";

        readonly ConcurrentQueue<Action> m_Main = new();
        ISpeechToText m_Speech;
        Func<string> m_Debug;
        UnityWebRequest m_Request;
        UnityWebRequest m_ClerkAudioRequest;
        AudioSource m_ClerkSpeaker;
        AudioClip m_ClerkAudio;
        readonly UnityWebRequest[] m_CaptionRequests = new UnityWebRequest[2];
        readonly string[] m_CaptionText = new string[2];

        [Serializable] sealed class CaptionTranslation { public string english; }
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
            StopClerkAudio();
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
            if (overlay) overlay.SetListening(false);
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
            if (!IsConversationActive && response.speech?.role == "clerk")
            {
                ShowDialogue(true, response.speech.text);
                PlayClerkAudio(response.speech, generation);
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
            int nextBeat = Beat + (response.advance == "next_line" ? 1 : 0);
            // Do not publish success until ALL native acknowledgements have completed.
            await visko.SendLearningEvent(LearningContract.SyncJson(json, m_Session, m_RunLesson, nextBeat));
            EnsureCurrent(generation);
            Beat = nextBeat;
            IsDone = response.advance == "scene_done";
            Present(response);
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
                if (response.operation == null || string.IsNullOrEmpty(response.operation.runId)) { m_NeedsRestart = true; return; }
                if (response.operation.runId == m_RunId && response.operation.revision <= m_Revision) return;
                if (IsConversationActive) { m_NeedsRestart = true; conversation.Disconnect(); return; }
                CancelSpeech();
                m_RunId = response.operation.runId; m_Revision = response.operation.revision;
                m_RunLesson = response.lessonId;
                Beat = response.beat; // Already advanced by the driving client.
                IsDone = response.advance == "scene_done";
                m_HasLesson = true;
                m_NeedsRestart = false;
                Present(response);
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
            m_Cancelling = true;
            conversation?.Disconnect();
            m_VoiceActive = m_VoiceConnecting = m_Reconciling = false;
            m_Tools.Clear(); m_VoiceTools.Clear();
            m_RunId = null; m_Revision = 0; m_Claims.Clear();
            m_Generation++;
            m_Cancelling = false;
            m_Request?.Abort();
            ClearDialogue();
            StopClerkAudio();
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
        }

        async void PlayClerkAudio(LearningSpeech speech, int generation)
        {
            if (string.IsNullOrWhiteSpace(speech.text)) return;
            StopClerkAudio();
            try
            {
                string url = visko.config.portalUrl.TrimEnd('/') + "/api/agent/clerk-speech";
                using var request = new UnityWebRequest(url, "POST");
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
                    if (!m_ClerkSpeaker)
                    {
                        m_ClerkSpeaker = gameObject.AddComponent<AudioSource>();
                        m_ClerkSpeaker.playOnAwake = false;
                        m_ClerkSpeaker.spatialBlend = 0;
                    }
                    m_ClerkSpeaker.clip = m_ClerkAudio;
                    m_ClerkSpeaker.Play();
                    Debug.Log("[Learning] Clerk speech started");
                }
                finally { if (m_ClerkAudioRequest == request) m_ClerkAudioRequest = null; }
            }
            catch (Exception) { Debug.LogWarning("[Learning] Clerk speech unavailable."); }
        }

        void ClearDialogue()
        {
            ShowDialogue(true, null);
            ShowDialogue(false, null);
        }

        async void ShowDialogue(bool clerk, string original)
        {
            if (!overlay) return;
            int slot = clerk ? 0 : 1;
            original = original?.Trim();
            if (m_CaptionText[slot] == original) return;
            m_CaptionText[slot] = original;
            m_CaptionRequests[slot]?.Abort();
            m_CaptionRequests[slot] = null;
            if (clerk) overlay.SetClerkCaption(original); else overlay.SetLearnerCaption(original);
            if (string.IsNullOrEmpty(original) || !visko || !isActiveAndEnabled) return;

            try
            {
                await visko.ConfigReady;
                if (!this || !isActiveAndEnabled || m_CaptionText[slot] != original) return;
                using var request = new UnityWebRequest(visko.config.portalUrl.TrimEnd('/') + "/api/agent/translate", "POST");
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(
                    "{\"text\":" + LearningContract.Quote(original.Substring(0, Math.Min(original.Length, 2000))) + "}"));
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
                    if (string.IsNullOrWhiteSpace(translation?.english)) return;
                    if (clerk) overlay.SetClerkCaption(original, translation.english);
                    else overlay.SetLearnerCaption(original, translation.english);
                    Debug.Log("[Learning] English caption displayed for " + (clerk ? "clerk" : "learner"));
                }
                finally { if (m_CaptionRequests[slot] == request) m_CaptionRequests[slot] = null; }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { Debug.LogWarning("[Learning] Dialogue translation unavailable."); }
        }

        void Present(LearningResponse response)
        {
            m_LastResponse = response;
            if (!overlay) return;
            overlay.Show(response.overlay, response.feedback, IsDone, response.focusWords);
            overlay.SetRoute(response.route?.kind == "detour", response.route?.label);
            if (!m_VoiceActive) ShowDialogue(true, response.speech?.text);
        }
        string BindingJson() => "{\"sessionId\":" + LearningContract.Quote(m_Session) + ",\"lessonId\":" + LearningContract.Quote(m_RunLesson) + "}";
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
