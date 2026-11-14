using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ImmerseTheBay.Net
{
    /// <summary>Attach beside NetClient to opt into the team protocol. Public methods/events run on Unity's main thread.</summary>
    [RequireComponent(typeof(NetClient))]
    [DisallowMultipleComponent]
    public class RealtimeSession : MonoBehaviour
    {
        public string defaultRoom = "bay";
        [Tooltip("Application ping interval; the NetClient echo probe/legacy heartbeat are disabled while this session runs.")]
        public float pingInterval = 5f;
        [Tooltip("Seconds to wait for welcome / room acknowledgement / pong before reconnecting.")]
        public float responseTimeout = 15f;

        public event Action<WelcomeData> OnWelcome;
        public event Action<MembershipData> OnJoined;
        public event Action<MembershipData> OnLeft;
        public event Action<StateMessage> OnState;
        public event Action<EventMessage> OnEvent;
        public event Action<PongData> OnPong;
        public event Action<RealtimeError> OnError;
        public event Action<BinaryMessage> OnBinary;
        public event Action<string> OnDisconnected;

        public bool IsReady { get; private set; }
        public string ClientId { get; private set; }
        public string Room { get; private set; }
        public IReadOnlyList<string> Members => m_Members;
        public float RttMs { get; private set; } = -1;
        public string Status { get; private set; } = "waiting for connection";

        NetClient m_Client;
        string m_LocalId;
        string m_RequestedRoom;
        string[] m_Members = Array.Empty<string>();
        string m_PingId;
        long m_PingTs;
        double m_PingStarted;
        double m_NextPing;
        double m_ResponseDeadline;
        bool m_RetryPending;
        bool m_Started;
        int m_Lifecycle;
        Func<string> m_Debug;

        void Awake()
        {
            m_Client = GetComponent<NetClient>();
            m_LocalId = Guid.NewGuid().ToString();
            m_Debug = () => $"<b>== REALTIME ==</b>\n{Status}\nclient: {ClientId ?? "-"} room: {Room ?? "-"}" +
                $"\nmembers ({m_Members.Length}): {string.Join(", ", m_Members)}" +
                (RttMs < 0 ? "\nRTT: waiting" : $"\nRTT: {RttMs:F0} ms");
        }

        void OnEnable()
        {
            m_Lifecycle++;
            // Opt-in session owns heartbeat/probe traffic, including when added after NetClient.Start.
            m_Client.sendTestMessages = false;
            m_Client.heartbeatInterval = 0;
            m_Client.OnConnected += Connected;
            m_Client.OnDisconnected += Disconnected;
            m_Client.OnMessage += HandleMessage;
            m_Client.OnBinary += HandleBinary;
            DebugRegistry.Register(m_Debug);
            if (m_Client.IsConnected) Connected();
            else if (m_Started && m_Client.isActiveAndEnabled && !string.IsNullOrEmpty(m_Client.webSocketUrl))
                _ = m_Client.Connect(m_Client.webSocketUrl);
        }

        void Start() => m_Started = true;

        void OnDisable()
        {
            m_Lifecycle++;
            m_Client.OnConnected -= Connected;
            m_Client.OnDisconnected -= Disconnected;
            m_Client.OnMessage -= HandleMessage;
            m_Client.OnBinary -= HandleBinary;
            DebugRegistry.Unregister(m_Debug);
            _ = m_Client.Disconnect();
            ResetSession();
        }

        void ResetSession()
        {
            IsReady = false;
            ClientId = null;
            Room = null;
            m_RequestedRoom = null;
            m_Members = Array.Empty<string>();
            m_PingId = null;
            RttMs = -1;
            m_ResponseDeadline = 0;
        }

        void Connected()
        {
            ResetSession();
            Status = "connected; awaiting welcome";
            m_ResponseDeadline = Time.realtimeSinceStartupAsDouble + Math.Max(1, responseTimeout);
            _ = Send("hello", JsonUtility.ToJson(new HelloData
            {
                clientId = m_LocalId,
                deviceModel = SystemInfo.deviceModel,
                appVersion = string.IsNullOrEmpty(Application.version) ? "unknown" : Application.version
            }));
        }

        void Disconnected(string reason)
        {
            ResetSession();
            Status = "disconnected: " + reason;
            OnDisconnected?.Invoke(reason);
        }

        void Update()
        {
            if (!m_Client.IsConnected || m_RetryPending) return;
            var now = Time.realtimeSinceStartupAsDouble;
            if ((m_ResponseDeadline > 0 && now >= m_ResponseDeadline) ||
                (m_PingId != null && now - m_PingStarted >= Math.Max(1, responseTimeout)))
            {
                _ = Retry();
                return;
            }
            if (!IsReady || pingInterval <= 0 || now < m_NextPing || m_PingId != null) return;
            m_PingId = Guid.NewGuid().ToString();
            m_PingTs = Protocol.NowMs;
            m_PingStarted = now;
            m_NextPing = now + Math.Max(0.1, pingInterval);
            _ = m_Client.SendText(Protocol.Write("ping", "{}", Room, ClientId, m_PingId, m_PingTs));
        }

        async Task Retry()
        {
            m_RetryPending = true;
            int lifecycle = m_Lifecycle;
            Status = m_Client.autoReconnect ? "response timed out; reconnecting" : "response timed out; disconnected";
            ResetSession();
            try
            {
                await m_Client.Disconnect();
                if (isActiveAndEnabled && lifecycle == m_Lifecycle && m_Client.autoReconnect)
                    _ = m_Client.Connect(m_Client.webSocketUrl);
            }
            finally { m_RetryPending = false; }
        }

        public Task JoinRoom(string room)
        {
            RequireReady();
            Protocol.ValidateName(room);
            m_RequestedRoom = room;
            m_ResponseDeadline = Time.realtimeSinceStartupAsDouble + Math.Max(1, responseTimeout);
            return Send("join", "{}", room);
        }

        public Task LeaveRoom()
        {
            RequireRoom();
            m_RequestedRoom = null;
            m_ResponseDeadline = Time.realtimeSinceStartupAsDouble + Math.Max(1, responseTimeout);
            return Send("leave", "{}", Room);
        }

        public Task SendState(string key, object data) => SendStateJson(key, Protocol.SerializeObject(data));
        public Task SendEvent(string name, object data) => SendEventJson(name, Protocol.SerializeObject(data));

        /// <summary>Use for arbitrary JSON values (including dictionaries/arrays) that JsonUtility cannot serialize.</summary>
        public Task SendStateJson(string key, string json)
        {
            RequireRoom();
            return Send("state", Protocol.NamedValue("key", key, json), Room);
        }

        public Task SendEventJson(string name, string json)
        {
            RequireRoom();
            return Send("event", Protocol.NamedValue("name", name, json), Room);
        }

        public Task SendBinary(BinaryKind kind, uint sampleRateOrWidth, byte[] payload)
        {
            RequireRoom();
            return m_Client.SendBinary(Protocol.EncodeBinary(kind, sampleRateOrWidth, payload));
        }

        void RequireReady()
        {
            if (!IsReady || !m_Client.IsConnected) throw new InvalidOperationException("Wait for realtime welcome before sending");
        }

        void RequireRoom()
        {
            RequireReady();
            if (string.IsNullOrEmpty(Room)) throw new InvalidOperationException("Join a room before sending");
        }

        Task Send(string type, string data, string room = null) => m_Client.SendText(Protocol.Write(type, data, room, ClientId));

        void HandleMessage(string json)
        {
            // Decode before invoking user callbacks, so subscriber exceptions aren't mistaken for protocol errors.
            Action dispatch;
            try { dispatch = Decode(Protocol.Read(json)); }
            catch (Exception e) when (e is FormatException || e is ArgumentException)
            {
                Status = "invalid server message: " + e.Message;
                OnError?.Invoke(new RealtimeError { code = "invalid_server_message", message = e.Message });
                return;
            }
            dispatch?.Invoke();
        }

        Action Decode(RealtimeEnvelope envelope)
        {
            switch (envelope.type)
            {
                case "welcome":
                    var welcome = envelope.ReadData<WelcomeData>();
                    if (welcome == null || string.IsNullOrEmpty(welcome.clientId) || welcome.members == null)
                        throw new FormatException("Invalid welcome");
                    return () =>
                    {
                        ClientId = welcome.clientId;
                        IsReady = true;
                        m_ResponseDeadline = 0;
                        m_Members = welcome.members;
                        Status = "connected";
                        m_NextPing = 0;
                        if (!string.IsNullOrWhiteSpace(defaultRoom)) _ = JoinRoom(defaultRoom);
                        OnWelcome?.Invoke(welcome);
                    };
                case "join":
                case "leave":
                    var membership = envelope.ReadData<MembershipData>();
                    if (membership == null || membership.members == null || string.IsNullOrEmpty(envelope.room))
                        throw new FormatException("Invalid membership");
                    return () =>
                    {
                        bool self = membership.clientId == ClientId;
                        if (envelope.type == "join" && self)
                        {
                            Room = envelope.room;
                            m_ResponseDeadline = 0;
                            m_RequestedRoom = null;
                        }
                        if (Room == envelope.room) m_Members = membership.members;
                        if (envelope.type == "leave" && self)
                        {
                            Room = null;
                            m_Members = Array.Empty<string>();
                            // A switch generates leave then join; keep waiting for the new join.
                            if (m_RequestedRoom == null) m_ResponseDeadline = 0;
                        }
                        Status = Room == null ? "connected; no room" : "connected; joined " + Room;
                        if (envelope.type == "join") OnJoined?.Invoke(membership);
                        else OnLeft?.Invoke(membership);
                    };
                case "state":
                case "event":
                    var fields = Protocol.ObjectFields(envelope.DataJson);
                    var name = Protocol.StringField(fields, envelope.type == "state" ? "key" : "name");
                    if (!fields.TryGetValue("value", out var value)) throw new FormatException("Missing value");
                    Protocol.ValidateJson(value);
                    return () =>
                    {
                        if (envelope.room != Room) return;
                        if (envelope.type == "state") OnState?.Invoke(new StateMessage { Envelope = envelope, Key = name, Json = value });
                        else OnEvent?.Invoke(new EventMessage { Envelope = envelope, Name = name, Json = value });
                    };
                case "pong":
                    var pong = envelope.ReadData<PongData>();
                    if (pong == null) throw new FormatException("Invalid pong");
                    return () =>
                    {
                        if (pong.replyTo != m_PingId || pong.clientTs != m_PingTs) return;
                        RttMs = (float)((Time.realtimeSinceStartupAsDouble - m_PingStarted) * 1000);
                        m_PingId = null;
                        OnPong?.Invoke(pong);
                    };
                case "error":
                    var error = envelope.ReadData<RealtimeError>();
                    if (error == null) throw new FormatException("Invalid error");
                    return () => { Status = "server error: " + error.code + " " + error.message; OnError?.Invoke(error); };
                default: throw new FormatException("Unknown server type: " + envelope.type);
            }
        }

        void HandleBinary(byte[] bytes)
        {
            BinaryMessage message;
            try { message = Protocol.DecodeBinary(bytes); }
            catch (FormatException e)
            {
                OnError?.Invoke(new RealtimeError { code = "invalid_binary", message = e.Message });
                return;
            }
            OnBinary?.Invoke(message);
        }
    }
}
