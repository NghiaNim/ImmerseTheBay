using System;
using System.Collections.Concurrent;
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
    public class ViskoSession : MonoBehaviour
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
        public string Model { get; private set; } = "";
        public bool IsConnected => Status == "READY";
        public event Action<string, string> OnModelMessage; // (type, json)

        const string Hello = "{\"type\":\"hello\",\"role\":\"unity\",\"name\":\"";
        readonly ConcurrentQueue<Action> m_Main = new();
        NetClient m_Hub;
        IViskoBackend m_Backend;
        Texture2D m_Texture;
        long m_LastFrame;
        bool m_OwnsSession;
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
#if UNITY_ANDROID && !UNITY_EDITOR
            m_Backend = new AndroidViskoBackend(this);
#else
            m_Backend = null; // Reactor's native SDK only runs on device; the editor still syncs with the portal.
#endif
        }

        void OnEnable() => DebugRegistry.Register(m_Debug);

        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            m_Backend?.Dispose();
        }

        async void Start()
        {
            await LoadConfig();
            m_Hub = gameObject.AddComponent<NetClient>();
            m_Hub.testUrl = "";
            m_Hub.sendTestMessages = false;
            m_Hub.heartbeatMessage = "{\"type\":\"ping\"}";
            m_Hub.OnConnected += () =>
            {
                m_HubStatus = "online";
                _ = m_Hub.SendText(Hello + Esc(SystemInfo.deviceModel) + "\"}");
                SendHubStatus();
                if (m_OwnsSession && SessionId != "") AnnounceSession();
            };
            m_Hub.OnDisconnected += _ => m_HubStatus = "offline";
            m_Hub.OnMessage += OnHubMessage;
            var ws = config.portalUrl.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/') + "/sync" +
                     (string.IsNullOrEmpty(config.portalToken) ? "" : "?portal_token=" + UnityWebRequest.EscapeURL(config.portalToken));
            _ = m_Hub.Connect(ws);
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
            m_FpsT += Time.unscaledDeltaTime;
            if (m_FpsT >= 1f) { m_Fps = m_Frames / m_FpsT; m_Frames = 0; m_FpsT = 0; }
        }

        // ------------------------------------------------------------ public API (menu / other scripts)
        public async void CreateSession() => await ConnectInternal(ModelFromConfig(), null);

        public async void JoinPortalSession()
        {
            if (string.IsNullOrEmpty(m_PortalSession)) { m_LastError = "no portal session announced"; return; }
            await ConnectInternal(Model == "" ? ModelFromConfig() : Model, m_PortalSession);
        }

        public void StartGeneration(string prompt = null)
        {
            // Always native 832x480: resolution applies at the next start, so set it right before starting.
            Command("set_resolution", "{\"resolution\":\"" + glassesResolution + "\"}");
            Command("set_prompt", "{\"prompt\":\"" + Esc(string.IsNullOrEmpty(prompt) ? config.defaultPrompt : prompt) + "\"}");
            Command("start", null);
        }

        public void SetPrompt(string prompt) => Command("set_prompt", "{\"prompt\":\"" + Esc(prompt) + "\"}");
        public void Pause() => Command("pause", null);
        public void Resume() => Command("resume", null);
        public void ResetGeneration() => Command("reset", null);

        public void Leave()
        {
            m_Backend?.Disconnect();
            if (m_OwnsSession) _ = m_Hub?.SendText("{\"type\":\"session_end\"}");
            m_OwnsSession = false; SessionId = ""; Status = "DISCONNECTED";
            SendHubStatus();
        }

        /// <summary>Sends a Reactor command. If this app is not connected, asks the web portal to run it.</summary>
        public void Command(string name, string argsJson)
        {
            if (m_Backend != null && IsConnected) { m_Backend.SendCommand(name, argsJson ?? ""); return; }
            _ = m_Hub?.SendText("{\"type\":\"command\",\"name\":\"" + Esc(name) + "\",\"data\":" + (string.IsNullOrEmpty(argsJson) ? "{}" : argsJson) + "}");
            m_LastMessage = $"→ portal {name}";
        }

        string ModelFromConfig() => config.model == "stable" || config.model == Slugs[1] ? Slugs[1] : Slugs[0];

        async Task ConnectInternal(string model, string sessionId)
        {
            if (m_Backend == null) { m_LastError = "Visko video needs the Android build (editor: use the web portal)"; return; }
            m_LastError = "";
            Model = model;
            m_OwnsSession = string.IsNullOrEmpty(sessionId);
            Status = "TOKEN";
            string jwt;
            try { jwt = await MintToken(model, sessionId); }
            catch (Exception e) { m_LastError = "token: " + e.Message; Status = "DISCONNECTED"; return; }
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
        internal void PostStatus(string s) => m_Main.Enqueue(() =>
        {
            Status = s;
            if (s == "READY") m_Retries = 0;
            SendHubStatus();
            if (s == "READY" && m_OwnsSession)
            {
                // Frames cross JNI every frame: keep the shared session at 832x480 unless changed in the portal.
                m_Backend?.SendCommand("set_resolution", "{\"resolution\":\"" + glassesResolution + "\"}");
                AnnounceSession();
            }
        });

        internal void PostSessionId(string id) => m_Main.Enqueue(() => { SessionId = id; if (m_OwnsSession && IsConnected) AnnounceSession(); });

        internal void PostMessage(string json) => m_Main.Enqueue(() =>
        {
            var type = JsonField(json, "type");
            if (type != "chunk_complete") m_LastMessage = Trim(json, 120);
            // Mirror model state to the portal so the web UI shows it even when it is not connected itself
            if (type == "state") _ = m_Hub?.SendText("{\"type\":\"state\",\"data\":" + (JsonObjectField(json, "data") ?? "{}") + "}");
            OnModelMessage?.Invoke(type, json);
        });

        internal void PostReply(string command, string json) => m_Main.Enqueue(() => m_LastMessage = $"{command} ↩ {Trim(json, 100)}");
        internal void PostError(string msg) => m_Main.Enqueue(() =>
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
            var type = JsonField(json, "type");
            var from = JsonField(json, "from");
            switch (type)
            {
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
                    if (from == "web" && IsConnected) m_Backend.SendCommand(JsonField(json, "name"), JsonObjectField(json, "data") ?? "");
                    break;
            }
        }

        void OnPortalSession(string json)
        {
            m_PortalSession = JsonField(json, "sessionId");
            var model = JsonField(json, "model");
            if (!string.IsNullOrEmpty(model)) Model = model;
            m_LastMessage = $"portal session {Short(m_PortalSession)}";
            if (config.autoJoin && !IsConnected && m_Backend != null && !string.IsNullOrEmpty(m_PortalSession))
                _ = ConnectInternal(Model, m_PortalSession);
        }

        void AnnounceSession() =>
            _ = m_Hub?.SendText($"{{\"type\":\"session\",\"model\":\"{Model}\",\"sessionId\":\"{Esc(SessionId)}\",\"owner\":\"unity\"}}");

        void SendHubStatus() => _ = m_Hub?.SendText($"{{\"type\":\"status\",\"status\":\"{(m_Backend == null ? "EDITOR" : Status)}\"}}");

        // ------------------------------------------------------------ video
        void PullFrame()
        {
            if (m_Backend == null || !screen) return;
            long id = m_Backend.FrameId;
            if (id == m_LastFrame) return;
            m_LastFrame = id;
            int w = m_Backend.Width, h = m_Backend.Height;
            if (w <= 0 || h <= 0) return;
            var px = m_Backend.GetFrame();
            if (px == null || px.Length != w * h * 4) return;
            if (!m_Texture || m_Texture.width != w || m_Texture.height != h)
            {
                if (m_Texture) Destroy(m_Texture);
                m_Texture = new Texture2D(w, h, TextureFormat.BGRA32, false) { wrapMode = TextureWrapMode.Clamp };
                var mat = screen.material;
                mat.mainTexture = m_Texture;
                mat.mainTextureScale = new Vector2(1, -1); // frames are top-down, Unity textures bottom-up
                mat.mainTextureOffset = new Vector2(0, 1);
                if (mat.HasProperty("_Layout")) mat.SetFloat("_Layout", 0);
                var t = screen.transform;
                t.localScale = new Vector3(t.localScale.x, t.localScale.x * h / w, 1);
            }
            m_Texture.LoadRawTextureData(px);
            m_Texture.Apply(false);
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
        void SendCommand(string name, string argsJson);
        void Disconnect();
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
            public Listener(ViskoSession s) : base("com.immersethebay.visko.ViskoReactorBridge$Listener") { m_S = s; }
            void onStatus(string status) => m_S.PostStatus(status);
            void onSessionId(string id) => m_S.PostSessionId(id);
            void onMessage(string json) => m_S.PostMessage(json);
            void onReply(string command, string json) => m_S.PostReply(command, json);
            void onError(string message) => m_S.PostError(message);
        }

        public AndroidViskoBackend(ViskoSession s) =>
            m_Bridge = new AndroidJavaObject("com.immersethebay.visko.ViskoReactorBridge", new Listener(s));

        public void Connect(string jwt, string model, string sessionId) => m_Bridge.Call("connect", jwt, model, sessionId ?? "");
        public void SendCommand(string name, string argsJson) => m_Bridge.Call("sendCommand", name, argsJson ?? "");
        public void Disconnect() => m_Bridge.Call("disconnect");
        public long FrameId => m_Bridge.Call<long>("getFrameId");
        public int Width => m_Bridge.Call<int>("getWidth");
        public int Height => m_Bridge.Call<int>("getHeight");
        public byte[] GetFrame() => m_Bridge.Call<byte[]>("getFrame");
        public void Dispose() { try { m_Bridge.Call("dispose"); } catch { } m_Bridge.Dispose(); }
    }
#endif
}
