using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ImmerseTheBay.Content;
using ImmerseTheBay.Net;
using UnityEngine;
using UnityEngine.Networking;

namespace ImmerseTheBay.Visko
{
    public enum ViskoModel { Dynamic, Stable }

    [Serializable]
    public class ViskoConfig
    {
        [Tooltip("Portal base URL reachable from the device, e.g. http://192.168.1.20:8790")]
        public string portalUrl = "http://127.0.0.1:8790";
        public string portalToken = "";
        public string model = "dynamic";
        [Tooltip("Join the session the web portal creates automatically.")]
        public bool autoJoin = true;
        public string defaultPrompt = "A slow aerial shot over the Golden Gate Bridge at sunrise, fog rolling under the deck, cinematic.";
    }

    /// <summary>
    /// Visko (Reactor) real-time video in the glasses, kept in sync with the web portal:
    ///  - Tokens come from the portal (/api/token); the Reactor API key never ships in the app.
    ///  - The portal's /sync hub shares the current session id, so web and Unity watch the same Reactor session.
    ///    Whoever creates a session announces it; the other side joins it with a session-bound token.
    ///  - Commands typed in the portal with "Send commands through Unity" are relayed here and executed.
    ///  - Video frames (BGRA) from the Android bridge are uploaded to a texture on <see cref="screen"/>; audio plays natively.
    /// Config: StreamingAssets/visko.json (see <see cref="ViskoConfig"/>).
    /// </summary>
    public partial class ViskoSession : MonoBehaviour
    {
        public Renderer screen;
        public ViskoConfig config = new();
        [Tooltip("Resolution requested when the glasses create the session (applies at next start).")]
        public string glassesResolution = "native";
        [Tooltip("Retries when Reactor reports no capacity (HTTP 429) on session creation.")]
        public int capacityRetries = 3;
        int m_Retries;
        string m_PendingModel, m_PendingSession;

        public string Status { get; private set; } = "idle";
        public string SessionId { get; private set; } = "";
        public string PortalSessionId => m_PortalSession;
        public Texture2D StartImageTexture { get; private set; }
        public bool HasLiveFrame { get; private set; }
        bool m_PreparingScene, m_AcceptSceneFrames;
        long m_SceneFrameFloor;
        float m_LiveBlend;
        const float LessonAspect = 16f / 9f;
        public string Model { get; private set; } = "";
        public bool IsConnected => Status == "READY";
        public event Action<string> OnLearnMessage;
        public bool IsConfigLoaded { get; private set; }
        readonly TaskCompletionSource<bool> m_ConfigReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ConfigReady => m_ConfigReady.Task;
        public event Action<string, string> OnModelMessage; // (type, json)

        const string Hello = "{\"type\":\"hello\",\"role\":\"unity\",\"name\":\"";
        readonly ConcurrentQueue<Action> m_Main = new();
        NetClient m_Hub;
        IViskoBackend m_Backend;
        Texture2D m_Texture;
        long m_LastFrame;
        bool m_OwnsSession, m_AudioMuted;
        float m_SceneGain = 1f;
        long m_SceneGainRevision;
        int m_ConnectVersion;
        long m_OperationId;
        Task m_ImageTask;
        sealed class PendingOperation
        {
            public string Name;
            public TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        readonly Dictionary<string, PendingOperation> m_Pending = new();
        string m_LastError = "", m_LastMessage = "", m_HubStatus = "offline", m_PortalSession = "";
        int m_Frames; float m_FpsT; float m_Fps;
        Func<string> m_Debug;

        static readonly string[] Slugs = { "reactor/visko-orbis-dynamic", "reactor/visko-orbis-stable" };
        public static string Slug(ViskoModel m) => Slugs[(int)m];

        void Awake()
        {
            m_Debug = () =>
                $"<b>== VISKO ==</b>\nhub {m_HubStatus}  portal {config.portalUrl}\n" +
                $"reactor {Status}  model {Model}  session {Short(SessionId)} {(m_OwnsSession ? "(owner)" : SessionId != "" ? "(joined)" : "")}\n" +
                $"video {(m_Texture ? $"{m_Texture.width}x{m_Texture.height}" : "-")} {m_Fps:F0} fps  portal session {Short(m_PortalSession)}\n" +
                $"last {Trim(m_LastMessage, 70)}" + (m_LastError != "" ? $"\n<color=#f88>{Trim(m_LastError, 90)}</color>" : "");
        }

        void EnsureBackend()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Backend == null && isActiveAndEnabled)
            {
                m_Backend = new AndroidViskoBackend(this);
                m_Backend.SetSceneGain(m_SceneGain, m_SceneGainRevision);
                if (m_AudioMuted) m_Backend.SetAudioEnabled(false);
            }
#endif
        }

        void OnEnable()
        {
            DebugRegistry.Register(m_Debug);
            EnsureBackend();
            SetHasVideo(false);
            if (IsConfigLoaded) InitializeHub();
        }

        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            Leave();
            if (m_Hub) m_Hub.enabled = false;
        }

        void OnApplicationPause(bool paused)
        {
            // Leaving the XR view already cancels the lesson; end the Reactor session too so it stops billing.
            if (paused && (m_Backend != null || SessionId != "")) Leave();
        }

        void OnDestroy()
        {
            m_ConfigReady.TrySetCanceled();
            if (m_Hub) Destroy(m_Hub);
            if (m_Texture) Destroy(m_Texture);
            if (StartImageTexture) Destroy(StartImageTexture);
        }

        async void Start()
        {
            await LoadConfig();
            if (!this) return;
            IsConfigLoaded = true;
            m_ConfigReady.TrySetResult(true);
            if (isActiveAndEnabled) InitializeHub();
        }

        void InitializeHub()
        {
            if (m_Hub) { m_Hub.enabled = true; return; }
            m_Hub = gameObject.AddComponent<NetClient>();
            m_Hub.loadStreamingAssetsConfig = false;
            m_Hub.testUrl = "";
            m_Hub.sendTestMessages = false;
            m_Hub.heartbeatMessage = "{\"type\":\"ping\"}";
            m_Hub.OnConnected += () =>
            {
                if (!isActiveAndEnabled) return;
                m_HubStatus = "online";
                _ = m_Hub.SendText(Hello + Esc(SystemInfo.deviceModel) + "\"}");
                SendHubStatus();
                if (m_OwnsSession && SessionId != "") AnnounceSession();
            };
            m_Hub.OnDisconnected += _ => m_HubStatus = "offline";
            m_Hub.OnMessage += OnHubMessage;
            m_Hub.webSocketUrl = config.portalUrl.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/') + "/sync" +
                     (string.IsNullOrEmpty(config.portalToken) ? "" : "?portal_token=" + UnityWebRequest.EscapeURL(config.portalToken));
            // NetClient.Start owns the first connect; enabling it later reconnects to this URL.
        }

        async Task LoadConfig()
        {
            try
            {
                var json = await NetClient.Get(ContentLibrary.ToUrl("visko.json"));
                JsonUtility.FromJsonOverwrite(json, config);
            }
            catch (Exception e) { Debug.LogWarning($"[Visko] visko.json not loaded ({e.Message}); using inspector values"); }
        }

        void Update()
        {
            while (m_Main.TryDequeue(out var a)) a();
            PullFrame();
            if (m_PreparingScene && HasLiveFrame && m_LiveBlend < 1f)
            {
                m_LiveBlend = Mathf.MoveTowards(m_LiveBlend, 1f, Time.unscaledDeltaTime * 4f);
                ApplyVideoBlend();
            }
            UpdateSceneOpenings();
            m_FpsT += Time.unscaledDeltaTime;
            if (m_FpsT >= 1f) { m_Fps = m_Frames / m_FpsT; m_Frames = 0; m_FpsT = 0; }
        }

        // ------------------------------------------------------------ public API (menu / other scripts)
        public async void CreateSession()
        {
            int version = m_ConnectVersion;
            await ConfigReady;
            if (this && isActiveAndEnabled && version == m_ConnectVersion) await ConnectInternal(ModelFromConfig(), null);
        }

        public async void JoinPortalSession()
        {
            if (string.IsNullOrEmpty(m_PortalSession)) { m_LastError = "no portal session announced"; return; }
            await ConnectInternal(Model == "" ? ModelFromConfig() : Model, m_PortalSession);
        }

        public async void StartGeneration(string prompt = null)
        {
            try
            {
                await CommandAsync("set_prompt", "{\"prompt\":\"" + Esc(string.IsNullOrEmpty(prompt) ? config.defaultPrompt : prompt) + "\"}");
                await CommandAsync("start", null);
            }
            catch (Exception e) { m_LastError = e.Message; }
        }

        public void SetPrompt(string prompt) => Command("set_prompt", "{\"prompt\":\"" + Esc(prompt) + "\"}");
        public void Pause() => Command("pause", null);
        public void Resume() => Command("resume", null);
        public void ResetGeneration() => Command("reset", null);

        /// <summary>
        /// Mutes or restores the generated audio on this device only; the Reactor session is unchanged. Speech
        /// input mutes it while the learner speaks, so the glasses' microphone does not hear the scene.
        /// </summary>
        public void SetSceneAudioMuted(bool muted)
        {
            m_AudioMuted = muted;
            m_Backend?.SetAudioEnabled(!muted);
        }

        /// <summary>Set native scene loudness without changing the microphone's mute state.</summary>
        public void SetSceneGain(float gain)
        {
            gain = Mathf.Clamp01(gain);
            if (Mathf.Approximately(m_SceneGain, gain)) return;
            m_SceneGain = gain;
            ++m_SceneGainRevision;
            m_Backend?.SetSceneGain(gain, m_SceneGainRevision);
        }

        public void Leave() => DisconnectBackend(keepOpening: false);

        void DisconnectBackend(bool keepOpening)
        {
            PrefetchSceneOpening(null, null);
            if (!keepOpening) ClearSceneOpening();
            ++m_ConnectVersion;
            CancelInvoke(nameof(RetryConnect));
            Status = "DISCONNECTED";
            var backend = m_Backend;
            m_Backend = null;
            FailPending("Visko connection closed");
            m_ImageTask = null;
            m_LastFrame = -1;
            m_AcceptSceneFrames = false;
            SetHasVideo(false);
            backend?.Dispose();
            if (m_OwnsSession) _ = m_Hub?.SendText("{\"type\":\"session_end\"}");
            m_OwnsSession = false; SessionId = ""; Status = "DISCONNECTED";
            SendHubStatus();
        }

        /// <summary>Legacy command entry point; disconnected/editor callers relay to the web.</summary>
        public async void Command(string name, string argsJson)
        {
            try
            {
                if (m_Backend != null && IsConnected) await CommandAsync(name, argsJson);
                else if (m_Hub && m_Hub.IsConnected)
                    await m_Hub.SendText("{\"type\":\"command\",\"name\":\"" + Esc(name) + "\",\"data\":" + (string.IsNullOrEmpty(argsJson) ? "{}" : argsJson) + "}");
                else throw new InvalidOperationException("No Reactor or portal connection");
            }
            catch (Exception e) { m_LastError = e.Message; }
        }

        /// <summary>Completes after a correlated native reply. Relay mode cannot acknowledge execution.</summary>
        public async Task CommandAsync(string name, string argsJson)
        {
            RequireReady();
            int version = m_ConnectVersion;
            if (name == "start")
            {
                if (m_ImageTask != null) await m_ImageTask;
                CheckVersion(version);
                await RunOperation("set_resolution", (id) => m_Backend.SendCommand(id, "set_resolution", "{\"resolution\":\"" + Esc(glassesResolution) + "\"}"));
                CheckVersion(version);
            }
            if (m_PreparingScene && (name == "reset" || name == "start"))
            {
                m_AcceptSceneFrames = false;
                SetHasVideo(false);
                if (name == "start") m_SceneFrameFloor = m_Backend.FrameId;
            }
            await RunOperation(name, (id) => m_Backend.SendCommand(id, name, argsJson ?? ""));
            if (name == "reset") m_ImageTask = null;
            if (m_PreparingScene && name == "start")
            {
                CheckVersion(version);
                m_AcceptSceneFrames = true;
            }
        }

        /// <summary>Uploads a JPEG and completes only after set_image returns image_accepted.</summary>
        public Task SetImage(byte[] jpg)
        {
            RequireReady();
            if (jpg == null || jpg.Length == 0 || jpg.Length > 16 * 1024 * 1024)
                throw new ArgumentException("JPEG must be between 1 byte and 16 MiB", nameof(jpg));
            if (m_ImageTask != null && !m_ImageTask.IsCompleted) throw new InvalidOperationException("Image upload already in progress");
            return m_ImageTask = UploadAndDisplayImage((byte[])jpg.Clone());
        }

        public Task SetImageFromStreamingAssets(string path)
        {
            RequireReady();
            if (m_ImageTask != null && !m_ImageTask.IsCompleted) throw new InvalidOperationException("Image upload already in progress");
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(":") ||
                Array.Exists(path.Replace('\\', '/').Split('/'), segment => segment == ".."))
                throw new ArgumentException("Use a path relative to StreamingAssets", nameof(path));
            return m_ImageTask = LoadAndSetImage(path, m_ConnectVersion);
        }

        async Task LoadAndSetImage(string path, int version)
        {
            byte[] bytes = await ReadSceneImage(path, version);
            CheckVersion(version);
            await UploadAndDisplayImage(bytes);
        }

        public Task<byte[]> PrefetchLessonScene(string sceneId)
        {
            string path = sceneId switch
            {
                "sandwiches" => "lessons/images/konbini-sandwiches.png",
                "drinks" => "lessons/images/konbini-drinks.png",
                "checkout" => "lessons/images/konbini-checkout-original.png",
                "cafe-order" or "cafe-pour" or "cafe-serve" or
                "dog-greet" or "dog-pet" or "dog-goodbye" or
                "date-meet" or "date-walk" or "date-cafe" or
                "store-sandwich" or "store-drink" or "store-checkout" => $"lessons/images/{sceneId}.jpg",
                _ => throw new ArgumentException("Unknown bundled lesson scene", nameof(sceneId)),
            };
            return ReadSceneImage(path, m_ConnectVersion);
        }

        async Task<byte[]> ReadSceneImage(string path, int version)
        {
            using var request = UnityWebRequest.Get(ContentLibrary.ToUrl(path));
            request.timeout = 20;
            var operation = request.SendWebRequest();
            while (!operation.isDone) { CheckVersion(version); await Task.Yield(); }
            if (request.result != UnityWebRequest.Result.Success) throw new IOException(request.error);
            CheckVersion(version);
            byte[] bytes = request.downloadHandler.data;
            if (bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024) throw new IOException("Image must be between 1 byte and 16 MiB");
            return bytes;
        }

        async Task UploadAndDisplayImage(byte[] bytes)
        {
            int version = m_ConnectVersion;
            bool png = bytes.Length > 8 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71;
            await RunOperation("set_image", id => m_Backend.UploadImage(id, bytes, png ? "photo.png" : "photo.jpg", png ? "image/png" : "image/jpeg"));
            CheckVersion(version);
            if (!m_PreparingScene || !StartImageTexture)
                DisplayStartImage(bytes);
        }

        /// <summary>Display the selected local image independently of the provider connection.</summary>
        public void PrepareScenePreview(byte[] image)
        {
            m_PreparingScene = true;
            m_AcceptSceneFrames = false;
            SetHasVideo(false);
            DisplayStartImage(image);
            ApplySurfaceAspect();
        }

        void DisplayStartImage(byte[] bytes)
        {
            Texture2D texture = null;
            if (bytes is { Length: > 0 })
            {
                texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { wrapMode = TextureWrapMode.Clamp };
                if (!ImageConversion.LoadImage(texture, bytes, true))
                {
                    Destroy(texture);
                    texture = null;
                    Debug.LogWarning("[Visko] Could not decode the scene preview");
                }
            }
            var old = StartImageTexture;
            StartImageTexture = texture;
            if (screen && screen.material.HasProperty("_EnvironmentTex"))
            {
                var mat = screen.material;
                mat.SetTexture("_EnvironmentTex", texture ? texture : Texture2D.blackTexture);
                Vector2 crop = texture && m_PreparingScene ? CropScale(texture.width, texture.height) : Vector2.one;
                mat.SetTextureScale("_EnvironmentTex", crop);
                mat.SetTextureOffset("_EnvironmentTex", (Vector2.one - crop) * 0.5f);
            }
            SetHasVideo(false);
            if (old) Destroy(old);
        }

        static Vector2 CropScale(int width, int height)
        {
            float aspect = (float)width / height;
            return new Vector2(Mathf.Min(1f, LessonAspect / aspect), Mathf.Min(1f, aspect / LessonAspect));
        }

        void ApplySurfaceAspect()
        {
            if (!screen) return;
            var t = screen.transform;
            float heightRatio = m_PreparingScene ? 1f / LessonAspect : (m_Texture ? (float)m_Texture.height / m_Texture.width : 1f / LessonAspect);
            t.localScale = new Vector3(t.localScale.x, t.localScale.x * heightRatio, 1f);
        }

        public Task SendLearningEvent(string json)
        {
            if (!isActiveAndEnabled || !m_Hub || !m_Hub.IsConnected) throw new InvalidOperationException("Learning sync is offline");
            if (JsonField(json, "type") != "learn") throw new ArgumentException("Expected a complete learn envelope", nameof(json));
            return m_Hub.SendText(json);
        }

        void RequireReady()
        {
            if (!isActiveAndEnabled || m_Backend == null || !IsConnected)
                throw new InvalidOperationException("Reactor command unavailable: Android connection must be READY");
        }

        void CheckVersion(int version)
        {
            if (!this || version != m_ConnectVersion) throw new OperationCanceledException("Visko connection changed");
            RequireReady();
        }

        async Task RunOperation(string name, Action<string> send)
        {
            RequireReady();
            string id = (++m_OperationId).ToString();
            var pending = new PendingOperation { Name = name };
            m_Pending.Add(id, pending);
            try
            {
                send(id);
                if (await Task.WhenAny(pending.Completion.Task, Task.Delay(95000)) != pending.Completion.Task)
                    throw new TimeoutException(name + " acknowledgement timed out");
                await pending.Completion.Task;
            }
            finally { m_Pending.Remove(id); }
        }

        void FailPending(string error)
        {
            var pending = new List<PendingOperation>(m_Pending.Values);
            m_Pending.Clear();
            foreach (var operation in pending) operation.Completion.TrySetException(new InvalidOperationException(error));
        }

        string ModelFromConfig() => config.model == "stable" || config.model == Slugs[1] ? Slugs[1] : Slugs[0];

        async Task ConnectInternal(string model, string sessionId)
        {
            EnsureBackend();
            if (m_Backend == null) { m_LastError = "Visko video needs the Android build (editor: use the web portal)"; return; }
            await ConfigReady;
            if (!this || !isActiveAndEnabled) return;
            // Cold startup and pre-ready capacity retries share the opening for this explicit course choice.
            DisconnectBackend(keepOpening: string.IsNullOrEmpty(sessionId) && string.IsNullOrEmpty(SessionId));
            EnsureBackend();
            int version = m_ConnectVersion;
            m_LastError = "";
            Model = model;
            m_OwnsSession = string.IsNullOrEmpty(sessionId);
            Status = "TOKEN";
            string jwt;
            try { jwt = await MintToken(model, sessionId); }
            catch (Exception e) { if (version == m_ConnectVersion) { m_LastError = "token: " + e.Message; Status = "DISCONNECTED"; } return; }
            if (!this || !isActiveAndEnabled || version != m_ConnectVersion) return;
            m_PendingModel = model; m_PendingSession = sessionId;
            m_Backend.Connect(jwt, model, sessionId);
        }

        async Task<string> MintToken(string model, string sessionId)
        {
            var body = "{\"model\":\"" + model + "\"" + (string.IsNullOrEmpty(sessionId) ? "" : ",\"sessionId\":\"" + Esc(sessionId) + "\"") + "}";
            using var req = new UnityWebRequest(config.portalUrl.TrimEnd('/') + "/api/token", "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(config.portalToken)) req.SetRequestHeader("portal_token", config.portalToken);
            req.timeout = 20;
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success) throw new Exception($"{req.responseCode} {req.downloadHandler.text}");
            var res = JsonUtility.FromJson<TokenResponse>(req.downloadHandler.text);
            if (string.IsNullOrEmpty(res.jwt)) throw new Exception("empty jwt");
            return res.jwt;
        }

        [Serializable] class TokenResponse { public string jwt; public long expires_at; }

        // ------------------------------------------------------------ backend callbacks (any thread)
        void EnqueueBackend(object sender, Action callback) => m_Main.Enqueue(() =>
        {
            if (isActiveAndEnabled && ReferenceEquals(sender, m_Backend)) callback();
        });

        internal void PostStatus(object sender, string s) => EnqueueBackend(sender, () =>
        {
            Status = s;
            if (s == "DISCONNECTED") FailPending("Reactor disconnected");
            if (s == "READY") m_Retries = 0;
            SendHubStatus();
            if (s == "READY" && m_OwnsSession)
            {
                // Frames cross JNI every frame: keep the shared session at 832x480 unless changed in the portal.
                Command("set_resolution", "{\"resolution\":\"" + Esc(glassesResolution) + "\"}");
                AnnounceSession();
            }
        });

        internal void PostSessionId(object sender, string id) => EnqueueBackend(sender, () => { SessionId = id; if (m_OwnsSession && IsConnected) AnnounceSession(); });

        internal void PostMessage(object sender, string json) => EnqueueBackend(sender, () =>
        {
            var type = JsonField(json, "type");
            if (type == "command_error")
            {
                var data = JsonObjectField(json, "data");
                string command = JsonField(data, "command"), reason = JsonField(data, "reason");
                m_LastError = command + ": " + reason;
                // Broadcasts may belong to another client. Only correlated onOperation settles tasks.
            }
            if (type != "chunk_complete") m_LastMessage = Trim(json, 120);
            // Mirror model state to the portal so the web UI shows it even when it is not connected itself
            if (type == "state") _ = m_Hub?.SendText("{\"type\":\"state\",\"data\":" + (JsonObjectField(json, "data") ?? "{}") + "}");
            OnModelMessage?.Invoke(type, json);
        });

        internal void PostOperation(object sender, string id, string command, string json, string error) => EnqueueBackend(sender, () =>
        {
            if (!m_Pending.TryGetValue(id, out var pending)) return;
            if (!string.IsNullOrEmpty(error)) pending.Completion.TrySetException(new InvalidOperationException(error));
            else pending.Completion.TrySetResult(true);
            m_LastMessage = $"{command} ↩ {Trim(json, 100)}";
        });
        internal void PostError(object sender, string msg) => EnqueueBackend(sender, () =>
        {
            m_LastError = msg;
            _ = m_Hub?.SendText("{\"type\":\"log\",\"text\":\"" + Esc(msg) + "\"}");
            if (msg.Contains("429") && msg.Contains("capacity") && m_Retries < capacityRetries)
            {
                m_Retries++;
                m_LastError = $"Reactor at capacity, retry {m_Retries}/{capacityRetries} in 5 s";
                Invoke(nameof(RetryConnect), 5f);
            }
        });

        void RetryConnect() => _ = ConnectInternal(m_PendingModel, m_PendingSession);

        // ------------------------------------------------------------ hub
        void OnHubMessage(string json)
        {
            if (!isActiveAndEnabled) return;
            var type = JsonField(json, "type");
            var from = JsonField(json, "from");
            switch (type)
            {
                case "learn":
                    OnLearnMessage?.Invoke(json);
                    break;
                case "welcome":
                    var sessionObj = JsonObjectField(json, "session");
                    if (!string.IsNullOrEmpty(sessionObj) && JsonField(sessionObj, "owner") == "web") OnPortalSession(sessionObj);
                    break;
                case "session":
                    if (from == "web") OnPortalSession(json);
                    break;
                case "session_end":
                    if (from == "web") { m_PortalSession = ""; if (!m_OwnsSession && SessionId != "") Leave(); }
                    break;
                case "command":
                    if (from == "web" && IsConnected) Command(JsonField(json, "name"), JsonObjectField(json, "data") ?? "");
                    break;
            }
        }

        void OnPortalSession(string json)
        {
            m_PortalSession = JsonField(json, "sessionId");
            var model = JsonField(json, "model");
            if (!string.IsNullOrEmpty(model)) Model = model;
            m_LastMessage = $"portal session {Short(m_PortalSession)}";
            if (config.autoJoin) EnsureBackend();
            if (config.autoJoin && (Status == "DISCONNECTED" || Status == "idle") && m_Backend != null && !string.IsNullOrEmpty(m_PortalSession))
                _ = ConnectInternal(Model, m_PortalSession);
        }

        void AnnounceSession() =>
            _ = m_Hub?.SendText($"{{\"type\":\"session\",\"model\":\"{Model}\",\"sessionId\":\"{Esc(SessionId)}\",\"owner\":\"unity\"}}");

        void SendHubStatus() => _ = m_Hub?.SendText($"{{\"type\":\"status\",\"status\":\"{(m_Backend == null ? "EDITOR" : Status)}\"}}");

        // ------------------------------------------------------------ video
        void SetHasVideo(bool value)
        {
            HasLiveFrame = value;
            if (!value || !m_PreparingScene) m_LiveBlend = value ? 1f : 0f;
            ApplyVideoBlend();
        }

        void ApplyVideoBlend()
        {
            if (screen && screen.material.HasProperty("_HasVideo")) screen.material.SetFloat("_HasVideo", m_LiveBlend);
        }

        void PullFrame()
        {
            if (m_Backend == null || !screen || (m_PreparingScene && !m_AcceptSceneFrames)) return;
            long id = m_Backend.FrameId;
            if (id == m_LastFrame || (m_PreparingScene && id <= m_SceneFrameFloor)) return;
            int w = m_Backend.Width, h = m_Backend.Height;
            if (w <= 0 || h <= 0) return;
            var px = m_Backend.GetFrame();
            if (px == null || px.Length != w * h * 4) return;
            m_LastFrame = id;
            if (!m_Texture || m_Texture.width != w || m_Texture.height != h)
            {
                if (m_Texture) Destroy(m_Texture);
                m_Texture = new Texture2D(w, h, TextureFormat.BGRA32, false) { wrapMode = TextureWrapMode.Clamp };
            }
            if (!HasLiveFrame || screen.material.mainTexture != m_Texture)
            {
                var mat = screen.material;
                var crop = m_PreparingScene ? CropScale(w, h) : Vector2.one;
                mat.mainTexture = m_Texture;
                mat.mainTextureScale = new Vector2(crop.x, -crop.y); // BGRA rows arrive top-down.
                mat.mainTextureOffset = new Vector2((1f - crop.x) * 0.5f, (1f + crop.y) * 0.5f);
                if (mat.HasProperty("_Layout")) mat.SetFloat("_Layout", 0);
                ApplySurfaceAspect();
            }
            m_Texture.LoadRawTextureData(px);
            m_Texture.Apply(false);
            if (!HasLiveFrame) SetHasVideo(true);
            m_Frames++;
        }

        // ------------------------------------------------------------ tiny JSON helpers (flat field access only)
        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        static string Short(string s) => string.IsNullOrEmpty(s) ? "-" : s.Length > 10 ? s.Substring(0, 10) + "…" : s;
        static string Trim(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n) + "…";

        /// <summary>Value of a top-level string field ("key":"value"), or "".</summary>
        public static string JsonField(string json, string key)
        {
            int i = FindKey(json, key);
            if (i < 0) return "";
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return "";
            var sb = new StringBuilder();
            for (i++; i < json.Length && json[i] != '"'; i++)
            {
                if (json[i] == '\\' && i + 1 < json.Length) { i++; sb.Append(json[i] == 'n' ? '\n' : json[i]); }
                else sb.Append(json[i]);
            }
            return sb.ToString();
        }

        /// <summary>Raw text of a top-level object field ("key":{...}), or null.</summary>
        public static string JsonObjectField(string json, string key)
        {
            int i = FindKey(json, key);
            if (i < 0) return null;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '{') return null;
            int depth = 0; bool str = false;
            for (int j = i; j < json.Length; j++)
            {
                char c = json[j];
                if (str) { if (c == '\\') j++; else if (c == '"') str = false; continue; }
                if (c == '"') str = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return json.Substring(i, j - i + 1);
            }
            return null;
        }

        static int FindKey(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return -1;
            var pat = "\"" + key + "\"";
            int depth = 0; bool str = false;
            for (int j = 0; j < json.Length; j++)
            {
                char c = json[j];
                if (str) { if (c == '\\') j++; else if (c == '"') str = false; continue; }
                if (c == '{' || c == '[') { depth++; continue; }
                if (c == '}' || c == ']') { depth--; continue; }
                if (c == '"')
                {
                    if (depth == 1 && string.CompareOrdinal(json, j, pat, 0, pat.Length) == 0)
                    {
                        int k = j + pat.Length;
                        while (k < json.Length && char.IsWhiteSpace(json[k])) k++;
                        if (k < json.Length && json[k] == ':') return k + 1;
                    }
                    str = true;
                }
            }
            return -1;
        }
    }

    // ---------------------------------------------------------------- backend abstraction
    interface IViskoBackend : IDisposable
    {
        void Connect(string jwt, string model, string sessionId);
        void SendCommand(string id, string name, string argsJson);
        void UploadImage(string id, byte[] bytes, string name, string mime);
        void Disconnect();
        void SetAudioEnabled(bool enabled);
        bool SetSceneGain(float gain, long revision);
        long FrameId { get; }
        int Width { get; }
        int Height { get; }
        byte[] GetFrame();
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    /// <summary>Talks to Assets/Plugins/Android/ViskoReactorBridge.kt.</summary>
    class AndroidViskoBackend : IViskoBackend
    {
        readonly AndroidJavaObject m_Bridge;

        class Listener : AndroidJavaProxy
        {
            readonly ViskoSession m_S;
            readonly AndroidViskoBackend m_Backend;
            public Listener(ViskoSession s, AndroidViskoBackend backend) : base("com.immersethebay.visko.ViskoReactorBridge$Listener") { m_S = s; m_Backend = backend; }
            void onStatus(string status) => m_S.PostStatus(m_Backend, status);
            void onSessionId(string id) => m_S.PostSessionId(m_Backend, id);
            void onMessage(string json) => m_S.PostMessage(m_Backend, json);
            void onOperation(string id, string command, string json, string error) => m_S.PostOperation(m_Backend, id, command, json, error);
            void onError(string message) => m_S.PostError(m_Backend, message);
        }

        public AndroidViskoBackend(ViskoSession s) =>
            m_Bridge = new AndroidJavaObject("com.immersethebay.visko.ViskoReactorBridge", new Listener(s, this));

        public void Connect(string jwt, string model, string sessionId) => m_Bridge.Call("connect", jwt, model, sessionId ?? "");
        public void SendCommand(string id, string name, string argsJson) => m_Bridge.Call("sendCommand", id, name, argsJson ?? "");
        public void UploadImage(string id, byte[] bytes, string name, string mime) => m_Bridge.Call("uploadImage", id, bytes, name, mime);
        public void Disconnect() => m_Bridge.Call("disconnect");
        public void SetAudioEnabled(bool enabled) => m_Bridge.Call("setAudioEnabled", enabled);
        public bool SetSceneGain(float gain, long revision) => m_Bridge.Call<bool>("setSceneGain", gain, revision);
        public long FrameId => m_Bridge.Call<long>("getFrameId");
        public int Width => m_Bridge.Call<int>("getWidth");
        public int Height => m_Bridge.Call<int>("getHeight");
        public byte[] GetFrame() => m_Bridge.Call<byte[]>("getFrame");
        public void Dispose() { try { m_Bridge.Call("dispose"); } catch { } m_Bridge.Dispose(); }
    }
#endif
}
