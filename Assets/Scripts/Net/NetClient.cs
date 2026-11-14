using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ImmerseTheBay.Net
{
    /// <summary>
    /// Networking toolkit:
    ///  - Real-time WebSocket client (System.Net.WebSockets, works under IL2CPP on Android) with auto-reconnect,
    ///    heartbeat, serialized sends, text + binary messages. Messages are delivered on the main thread.
    ///  - Async HTTP GET / POST JSON helpers (UnityWebRequest).
    /// </summary>
    public class NetClient : MonoBehaviour
    {
        [Tooltip("Requested once on start to prove HTTP connectivity. Empty = skip.")]
        public string testUrl = "https://httpbin.org/get";

        [Header("WebSocket")]
        [Tooltip("Connected on start, e.g. wss://your-server/ws. Empty = don't connect.")]
        public string webSocketUrl = "";
        public bool autoReconnect = true;
        [Tooltip("Seconds between reconnect attempts (doubles up to 30s).")]
        public float reconnectDelay = 1f;
        [Tooltip("Sends this text every N seconds to keep NATs / proxies open. 0 = off.")]
        public float heartbeatInterval = 15f;
        public string heartbeatMessage = "{\"type\":\"ping\"}";
        [Tooltip("Sends a message every second on connect (useful with an echo server to check latency).")]
        public bool sendTestMessages = true;
        [Tooltip("Load StreamingAssets/net.json before connecting on start; missing/invalid config uses webSocketUrl.")]
        public bool loadStreamingAssetsConfig = true;

        /// <summary>Text message received (main thread).</summary>
        public event Action<string> OnMessage;
        /// <summary>Binary message received (main thread).</summary>
        public event Action<byte[]> OnBinary;
        public event Action OnConnected;
        public event Action<string> OnDisconnected;

        public bool IsConnected => m_Ws?.State == WebSocketState.Open;
        public string LastHttp { get; private set; } = "-";
        public string WsStatus => m_Ws == null ? "not connected" : m_Ws.State.ToString();

        ClientWebSocket m_Ws;
        CancellationTokenSource m_Cts;
        Task m_ConnectionLoop = Task.CompletedTask;
        int m_ConnectionGeneration;
        bool m_Started;
        readonly SemaphoreSlim m_SendLock = new(1, 1);
        readonly SemaphoreSlim m_LifecycleLock = new(1, 1);
        readonly ConcurrentQueue<Action> m_MainThread = new();
        int m_RxCount, m_TxCount, m_Reconnects;
        string m_LastRx = "";
        float m_LatencyMs = -1;
        bool m_WantConnected;
        Func<string> m_Debug;

        void Awake()
        {
            m_Debug = () =>
                $"<b>== NETWORK ==</b>\nreachability: {Application.internetReachability}\nHTTP: {LastHttp}\n" +
                $"WS: {WsStatus} {webSocketUrl}\n tx={m_TxCount} rx={m_RxCount} reconnects={m_Reconnects}" +
                (m_LatencyMs >= 0 ? $" echo RTT={m_LatencyMs:F0} ms" : "") + $"\n last={Trim(m_LastRx, 60)}";
        }

        void OnEnable()
        {
            DebugRegistry.Register(m_Debug);
            if (m_Started)
            {
                if (!string.IsNullOrEmpty(webSocketUrl)) _ = Connect(webSocketUrl);
                ScheduleMessages();
            }
        }

        async void Start()
        {
            if (loadStreamingAssetsConfig) await LoadConfig();
            m_Started = true;
            if (!isActiveAndEnabled) return;
            if (!string.IsNullOrEmpty(webSocketUrl)) _ = Connect(webSocketUrl);
            ScheduleMessages();

            if (!string.IsNullOrEmpty(testUrl))
            {
                var t0 = Time.realtimeSinceStartup;
                try
                {
                    var body = await Get(testUrl);
                    LastHttp = $"GET OK {(Time.realtimeSinceStartup - t0) * 1000:F0} ms, {body.Length} bytes";
                }
                catch (Exception e) { LastHttp = $"<color=#f88>GET failed: {e.Message}</color>"; }
            }
        }

        void ScheduleMessages()
        {
            CancelInvoke();
            if (sendTestMessages) InvokeRepeating(nameof(SendTestMessage), 2f, 1f);
            if (heartbeatInterval > 0) InvokeRepeating(nameof(Heartbeat), heartbeatInterval, heartbeatInterval);
        }

        [Serializable] class NetConfig { public string url = ""; }

        async Task LoadConfig()
        {
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, "net.json");
                var url = path.Contains("://") ? path : "file://" + path;
                var config = JsonUtility.FromJson<NetConfig>(await Get(url, 5));
                if (config != null && Uri.TryCreate(config.url, UriKind.Absolute, out var endpoint) &&
                    (endpoint.Scheme == "ws" || endpoint.Scheme == "wss"))
                    webSocketUrl = config.url;
                else Debug.LogWarning("[Net] Invalid net.json URL; using inspector webSocketUrl");
            }
            catch (Exception)
            {
                // Optional configuration; UnityWebRequest also reads jar:file:// paths inside Android APKs.
                Debug.Log("[Net] net.json unavailable; using inspector webSocketUrl");
            }
        }

        void Update()
        {
            while (m_MainThread.TryDequeue(out var a)) a();
        }

        // ------------------------------------------------------------ WebSocket
        /// <summary>Connects (and keeps reconnecting if <see cref="autoReconnect"/>) until <see cref="Disconnect"/>.</summary>
        public async Task Connect(string url)
        {
            Task loop;
            await m_LifecycleLock.WaitAsync();
            try
            {
                await StopConnection();
                webSocketUrl = url;
                m_WantConnected = true;
                m_Cts = new CancellationTokenSource();
                m_LatencyMs = -1;
                loop = m_ConnectionLoop = RunConnection(url, m_Cts.Token, m_ConnectionGeneration);
            }
            finally { m_LifecycleLock.Release(); }
            await loop;
        }

        async Task RunConnection(string url, CancellationToken token, int generation)
        {
            float delay = reconnectDelay;

            while (m_WantConnected && !token.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                m_Ws = ws;
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                string reason;
                try
                {
                    await ws.ConnectAsync(new Uri(url), token);
                    delay = reconnectDelay;
                    m_MainThread.Enqueue(() => { if (generation == m_ConnectionGeneration) OnConnected?.Invoke(); });
                    reason = await ReceiveLoop(ws, token, generation);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { reason = e.Message; }
                finally { if (ReferenceEquals(m_Ws, ws)) m_Ws = null; }

                m_MainThread.Enqueue(() =>
                {
                    if (generation != m_ConnectionGeneration) return;
                    m_LastRx = $"[disconnected] {reason}";
                    OnDisconnected?.Invoke(reason);
                });
                if (!autoReconnect || !m_WantConnected) break;
                try { await Task.Delay(TimeSpan.FromSeconds(delay), token); } catch (OperationCanceledException) { break; }
                delay = Mathf.Min(delay * 2f, 30f);
                Interlocked.Increment(ref m_Reconnects);
            }
        }

        async Task<string> ReceiveLoop(ClientWebSocket ws, CancellationToken token, int generation)
        {
            var buf = new byte[64 * 1024];
            using var ms = new System.IO.MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), token);
                if (r.MessageType == WebSocketMessageType.Close) return $"closed by server ({r.CloseStatus})";
                if (ms.Length + r.Count > Protocol.MaxMessageBytes) return "message exceeds 1 MiB";
                ms.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var data = ms.ToArray();
                ms.SetLength(0);
                if (r.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(data);
                    m_MainThread.Enqueue(() => { if (generation == m_ConnectionGeneration) HandleText(text); });
                }
                else m_MainThread.Enqueue(() =>
                {
                    if (generation != m_ConnectionGeneration) return;
                    m_RxCount++;
                    m_LastRx = $"[binary {data.Length} B]";
                    OnBinary?.Invoke(data);
                });
            }
            return $"state {ws.State}";
        }

        void HandleText(string text)
        {
            m_RxCount++;
            m_LastRx = text;
            // Echo-latency probe: {"type":"probe","t":<ms>}
            const string probe = "{\"type\":\"probe\",\"t\":";
            if (text.StartsWith(probe) && double.TryParse(text.Substring(probe.Length).TrimEnd('}'),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t))
                m_LatencyMs = (float)(Time.realtimeSinceStartupAsDouble * 1000.0 - t);
            OnMessage?.Invoke(text);
        }

        public Task SendText(string text) => SendRaw(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text);
        public Task SendBinary(byte[] data) => SendRaw(data, WebSocketMessageType.Binary);
        /// <summary>Serialises any [Serializable] object with JsonUtility and sends it as text.</summary>
        public Task SendJson(object obj) => SendText(JsonUtility.ToJson(obj));

        async Task SendRaw(byte[] data, WebSocketMessageType type)
        {
            var ws = m_Ws;
            var cts = m_Cts;
            if (ws?.State != WebSocketState.Open) return;
            await m_SendLock.WaitAsync();
            try
            {
                if (ws.State != WebSocketState.Open || cts == null || cts.IsCancellationRequested) return;
                await ws.SendAsync(new ArraySegment<byte>(data), type, true, cts.Token);
                Interlocked.Increment(ref m_TxCount);
            }
            catch (Exception e) { m_MainThread.Enqueue(() => m_LastRx = $"[send error] {e.Message}"); }
            finally { m_SendLock.Release(); }
        }

        void SendTestMessage()
        {
            if (sendTestMessages && IsConnected) _ = SendText($"{{\"type\":\"probe\",\"t\":{(Time.realtimeSinceStartupAsDouble * 1000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}}}");
        }

        void Heartbeat()
        {
            if (heartbeatInterval > 0 && IsConnected && !string.IsNullOrEmpty(heartbeatMessage)) _ = SendText(heartbeatMessage);
        }

        public async Task Disconnect()
        {
            await m_LifecycleLock.WaitAsync();
            try { await StopConnection(); }
            finally { m_LifecycleLock.Release(); }
        }

        async Task StopConnection()
        {
            m_WantConnected = false;
            m_ConnectionGeneration++;
            var ws = m_Ws;
            var cts = m_Cts;
            cts?.Cancel();
            // Abort wakes ReceiveAsync immediately, even when the peer has disappeared.
            ws?.Abort();
            await m_ConnectionLoop;
            cts?.Dispose();
            if (ReferenceEquals(m_Cts, cts)) m_Cts = null;
        }

        // ------------------------------------------------------------ HTTP
        public static async Task<string> Get(string url, int timeoutSec = 15)
        {
            using var req = UnityWebRequest.Get(url);
            req.timeout = timeoutSec;
            await Send(req);
            return req.downloadHandler.text;
        }

        public static async Task<string> PostJson(string url, string json, string bearerToken = null, int timeoutSec = 30)
        {
            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(bearerToken)) req.SetRequestHeader("Authorization", "Bearer " + bearerToken);
            req.timeout = timeoutSec;
            await Send(req);
            return req.downloadHandler.text;
        }

        static Task Send(UnityWebRequest req)
        {
            var tcs = new TaskCompletionSource<bool>();
            req.SendWebRequest().completed += _ =>
            {
                if (req.result == UnityWebRequest.Result.Success) tcs.SetResult(true);
                else tcs.SetException(new Exception($"{req.responseCode} {req.error}"));
            };
            return tcs.Task;
        }

        void OnDisable()
        {
            DebugRegistry.Unregister(m_Debug);
            CancelInvoke();
            _ = Disconnect();
        }

        static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";
    }
}
