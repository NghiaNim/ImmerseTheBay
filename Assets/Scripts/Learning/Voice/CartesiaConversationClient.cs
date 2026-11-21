using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using ImmerseTheBay.Audio;

namespace ImmerseTheBay.Learning.Voice
{
    [Serializable] public sealed class VoiceToolParameters { public string intent; public string transcript; }
    [Serializable] public sealed class VoiceToolCall
    {
        public string tool_call_id, tool_name;
        public VoiceToolParameters parameters;
        public bool expects_response;
    }

    /// <summary>Managed Cartesia PCM transport. Call public methods on Unity's main thread.
    /// No acoustic echo cancellation: use headphones/glasses audio and verify on device.</summary>
    public sealed class CartesiaConversationClient : MonoBehaviour
    {
        public event Action<VoiceToolCall> ToolCall;
        public event Action<string, string> Transcript;
        public event Action<string> StateChanged;
        public event Action Interrupted;
        public bool IsConnected { get; private set; }
        public string Status { get; private set; } = "disconnected";
        const int Rate = 44100, MaxMessage = 1024 * 1024, MaxPending = 256;
        readonly ConcurrentQueue<Action> main = new ConcurrentQueue<Action>();
        readonly object audioLock = new object();
        readonly float[] playback = new float[Rate * 4];
        int read, write, available;
        Session current;
        MicrophoneCapture microphone;
        AudioSource speaker;
        AudioClip outputClip;
        readonly List<MicrophoneCapture> restore = new List<MicrophoneCapture>();
        Func<string> debug;
        float readyAt, connectAt;

        sealed class Session
        {
            public readonly object Gate = new object();
            public volatile bool Finished;
            public string Terminal;
            public bool MicrophoneFailurePending;
            public readonly ClientWebSocket Socket = new ClientWebSocket();
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            public readonly ConcurrentQueue<string> Outgoing = new ConcurrentQueue<string>();
            public readonly SemaphoreSlim Signal = new SemaphoreSlim(0);
        }
        [Serializable] sealed class ServerEvent
        {
            public string type, audio, role, text, message;
            public bool fatal;
        }
        [Serializable] sealed class AudioInput { public string type = "audio_input"; public string audio; }
        [Serializable] sealed class ToolResult
        {
            public string type = "client_tool_result";
            public string tool_call_id, result;
            public bool is_error;
        }
        void Awake()
        {
            debug = () => "<b>== CONVERSATION ==</b>\n" + Status + "\nPCM 44100 • device echo cancellation unverified";
            var child = new GameObject("Conversation microphone");
            child.SetActive(false);
            child.transform.SetParent(transform, false);
            microphone = child.AddComponent<MicrophoneCapture>();
            microphone.startOnEnable = false;
            microphone.sampleRate = Rate;
            microphone.chunkMs = 40;
            child.SetActive(true);
            microphone.OnPcmChunk += SendPcm;
            speaker = gameObject.AddComponent<AudioSource>();
            speaker.playOnAwake = false;
            speaker.loop = true;
            speaker.spatialBlend = 0;
            outputClip = AudioClip.Create("Conversation output", Rate, 1, Rate, true, ReadAudio);
            speaker.clip = outputClip;
        }
        void OnEnable() { DebugRegistry.Register(debug); }
        void OnDisable() { DebugRegistry.Unregister(debug); Disconnect(); }
        void OnApplicationPause(bool paused) { if (paused) Disconnect(); }
        void OnDestroy()
        {
            if (microphone) { microphone.OnPcmChunk -= SendPcm; Destroy(microphone.gameObject); }
            if (outputClip) Destroy(outputClip);
            if (speaker) Destroy(speaker);
        }
        public void Connect(string url, string token)
        {
            Disconnect();
            if (!isActiveAndEnabled) { SetStatus("conversation component disabled"); return; }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "wss" ||
                (uri.Host != "api.cartesia.ai" && uri.Host != "api-us.cartesia.ai" && uri.Host != "api-eu.cartesia.ai") ||
                !uri.AbsolutePath.StartsWith("/v1/agents/websocket/", StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort || string.IsNullOrEmpty(token))
            { SetStatus("invalid voice session"); return; }
            foreach (var capture in FindObjectsByType<MicrophoneCapture>(FindObjectsSortMode.None))
                if (capture != microphone && capture.IsCaptureRequested) { restore.Add(capture); capture.StopCapture(); }
            var session = new Session();
            current = session;
            connectAt = Time.realtimeSinceStartup;
            SetStatus("connecting", "connecting");
            // Single-use session token is never retained, logged or automatically retried.
            var builder = new UriBuilder(uri);
            builder.Query = (string.IsNullOrEmpty(uri.Query) ? "" : uri.Query.Substring(1) + "&") +
                "cartesia_version=2026-08-14&access_token=" + Uri.EscapeDataString(token);
            _ = Run(session, builder.Uri);
        }
        public void Disconnect()
        {
            var session = current;
            current = null;
            IsConnected = false;
            Stop(session);
            if (microphone) microphone.StopCapture();
            if (speaker) speaker.Stop();
            FlushAudio();
            foreach (var capture in restore) if (capture && capture.isActiveAndEnabled) capture.StartCapture();
            restore.Clear();
            SetStatus("disconnected", "off");
        }
        public void SendToolResult(string callId, string result, bool isError = false)
        {
            if (string.IsNullOrEmpty(callId) || Encoding.UTF8.GetByteCount(result ?? "") > 16000) return;
            Queue(current, JsonUtility.ToJson(new ToolResult { tool_call_id = callId, result = result ?? "", is_error = isError }));
        }
        void SetStatus(string value, string state = "off") { Status = value; StateChanged?.Invoke(state); }
        void Update()
        {
            for (int i = 0; i < 64 && main.TryDequeue(out var action); i++) action();
            if (current != null && current.Finished)
            { string terminal = current.Terminal; Disconnect(); SetStatus(terminal); }
            if (current != null && !IsConnected && Time.realtimeSinceStartup - connectAt > 20)
            { Disconnect(); SetStatus("voice session readiness timed out"); }
            if (IsConnected && Time.realtimeSinceStartup - readyAt > 18 && !microphone.IsRecording)
            { Disconnect(); SetStatus("microphone unavailable or permission denied"); }
        }
        void Post(Session session, Action action)
        {
            if (main.Count >= MaxPending) { Stop(session); return; }
            main.Enqueue(() => { if (current == session && !session.Finished) action(); });
        }
        static void Stop(Session session)
        {
            if (session == null) return;
            lock (session.Gate)
            {
                if (session.Finished) return;
                session.Cancel.Cancel();
                session.Socket.Abort();
            }
        }
        bool Queue(Session session, string json)
        {
            if (session == null) return false;
            lock (session.Gate)
            {
                if (session.Finished || session.Cancel.IsCancellationRequested) return false;
                if (session.Outgoing.Count >= 32) { Stop(session); return false; }
                session.Outgoing.Enqueue(json);
                session.Signal.Release();
                return true;
            }
        }
        void SendPcm(short[] samples, int rate)
        {
            if (!IsConnected) return;
            if (rate != Rate)
            {
                // MicrophoneCapture defers StopCapture while emitting this callback. Restore
                // other captures only after this emission has unwound and native capture stops.
                var session = current;
                if (session != null && !session.MicrophoneFailurePending)
                {
                    session.MicrophoneFailurePending = true;
                    Post(session, () => { Disconnect(); SetStatus("microphone requires 44100 Hz for this voice session"); });
                }
                return;
            }
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++) { bytes[i * 2] = (byte)samples[i]; bytes[i * 2 + 1] = (byte)(samples[i] >> 8); }
            Queue(current, JsonUtility.ToJson(new AudioInput { audio = Convert.ToBase64String(bytes) }));
        }
        async Task Run(Session session, Uri uri)
        {
            Task sending = null;
            string terminal = "voice disconnected; reconnect for a fresh session";
            try
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Cancel.Token))
                {
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    await session.Socket.ConnectAsync(uri, deadline.Token).ConfigureAwait(false);
                }
                Queue(session, "{\"type\":\"session_create\",\"audio\":{\"input_format\":\"pcm_44100\",\"output_delivery\":\"speaking_pace\",\"continuous_input\":true}}");
                sending = SendLoop(session);
                var buffer = new byte[16384];
                while (!session.Cancel.IsCancellationRequested)
                {
                    using (var message = new MemoryStream())
                    {
                        WebSocketReceiveResult received;
                        do
                        {
                            received = await session.Socket.ReceiveAsync(new ArraySegment<byte>(buffer), session.Cancel.Token).ConfigureAwait(false);
                            if (received.MessageType == WebSocketMessageType.Close) return;
                            if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > MaxMessage)
                                throw new InvalidDataException();
                            message.Write(buffer, 0, received.Count);
                        } while (!received.EndOfMessage);
                        string json = Encoding.UTF8.GetString(message.ToArray());
                        Post(session, () => Handle(session, json));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { terminal = "voice connection failed; request a fresh session"; }
            finally
            {
                Stop(session);
                if (sending != null) { try { await sending.ConfigureAwait(false); } catch (Exception) { } }
                lock (session.Gate)
                {
                    session.Terminal = terminal;
                    session.Finished = true;
                    session.Socket.Dispose();
                    session.Signal.Dispose();
                    session.Cancel.Dispose();
                }
            }
        }
        async Task SendLoop(Session session)
        {
            try
            {
                while (!session.Cancel.IsCancellationRequested)
                {
                    await session.Signal.WaitAsync(session.Cancel.Token).ConfigureAwait(false);
                    if (session.Outgoing.TryDequeue(out var json))
                    {
                        var bytes = Encoding.UTF8.GetBytes(json);
                        await session.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, session.Cancel.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception) { Stop(session); throw; }
        }
        void Handle(Session session, string json)
        {
            try
            {
                var ev = JsonUtility.FromJson<ServerEvent>(json);
                switch (ev.type)
                {
                    case "session_ready":
                        IsConnected = true; readyAt = Time.realtimeSinceStartup;
                        speaker.Play(); microphone.StartCapture(); SetStatus("listening", "listening"); break;
                    case "audio_output": AddAudio(Convert.FromBase64String(ev.audio)); break;
                    case "audio_output_clear":
                        speaker.Stop(); FlushAudio(); speaker.Play();
                        SetStatus("listening", "listening"); Interrupted?.Invoke(); break;
                    case "turn_started":
                        SetStatus(ev.role == "assistant" ? "speaking" : "listening", ev.role == "assistant" ? "speaking" : "listening"); break;
                    case "client_tool_call": ToolCall?.Invoke(JsonUtility.FromJson<VoiceToolCall>(json)); break;
                    case "turn_ended": Transcript?.Invoke(ev.role, ev.text ?? ""); break;
                    case "error":
                        if (ev.fatal) { Disconnect(); SetStatus("voice provider ended the session"); }
                        else SetStatus("voice provider reported a recoverable error", "listening");
                        break;
                }
            }
            catch (Exception) { Disconnect(); SetStatus("invalid voice event"); }
        }
        void AddAudio(byte[] bytes)
        {
            if ((bytes.Length & 1) != 0) throw new InvalidDataException();
            lock (audioLock)
            {
                if (bytes.Length / 2 > playback.Length - available) throw new InvalidDataException("Voice playback queue exceeded four seconds");
                for (int i = 0; i < bytes.Length; i += 2)
                {
                    playback[write] = LearningAgentClient.BoostSpeechSample((short)(bytes[i] | bytes[i + 1] << 8) / 32768f);
                    write = (write + 1) % playback.Length; available++;
                }
            }
        }
        void ReadAudio(float[] data)
        {
            lock (audioLock)
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = available > 0 ? playback[read] : 0;
                    if (available > 0) { read = (read + 1) % playback.Length; available--; }
                }
        }
        void FlushAudio() { lock (audioLock) { read = write = available = 0; } }
    }
}
