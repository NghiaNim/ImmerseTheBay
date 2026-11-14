using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace ImmerseTheBay.Cast
{
    /// <summary>One publisher connection. Call SendFrameAsync from a single worker at a time.</summary>
    internal sealed class CastSocket : IDisposable
    {
        readonly Uri m_Uri;
        readonly Action<string> m_Status;
        readonly CancellationTokenSource m_Stop = new();
        readonly object m_Gate = new();
        ClientWebSocket m_Socket;
        bool m_Disposed, m_RunFinished, m_StopDisposed;
        int m_Sends;
        volatile bool m_Connected;

        public bool IsConnected => m_Connected;

        // Status callbacks run on a worker; the caller must queue them for Update.
        public CastSocket(Uri uri, Action<string> status)
        {
            m_Uri = uri;
            m_Status = status;
            _ = Task.Run(RunAsync);
        }

        async Task RunAsync()
        {
            var token = m_Stop.Token;
            int delay = 1;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    using var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    lock (m_Gate)
                    {
                        if (m_Disposed) return;
                        m_Socket = ws;
                    }
                    try
                    {
                        m_Status("connecting");
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(10));
                            await ws.ConnectAsync(m_Uri, timeout.Token).ConfigureAwait(false);
                        }
                        m_Connected = true;
                        m_Status("connected");
                        delay = 1;
                        var buffer = new byte[4096];
                        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                        }
                        m_Status("disconnected; retrying");
                    }
                    catch (Exception e) when (!token.IsCancellationRequested)
                    {
                        // Avoid including the URI (which can contain a portal token) in HUD/logs.
                        m_Status($"retrying ({e.GetType().Name})");
                    }
                    finally
                    {
                        m_Connected = false;
                        lock (m_Gate)
                        {
                            if (ReferenceEquals(m_Socket, ws)) m_Socket = null;
                        }
                        ws.Abort();
                    }
                    await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                    delay = Math.Min(delay * 2, 15);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) when (token.IsCancellationRequested) { }
            finally
            {
                lock (m_Gate)
                {
                    m_RunFinished = true;
                    DisposeStopIfIdle();
                }
            }
        }

        public async Task<bool> SendFrameAsync(byte[] jpeg)
        {
            ClientWebSocket ws;
            CancellationToken token;
            lock (m_Gate)
            {
                if (m_Disposed || !m_Connected || m_Socket == null) return false;
                ws = m_Socket;
                token = m_Stop.Token;
                m_Sends++;
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await ws.SendAsync(new ArraySegment<byte>(jpeg), WebSocketMessageType.Binary, true, timeout.Token)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                // Interrupt ReceiveAsync so the connection loop can reconnect after a failed send.
                lock (m_Gate)
                {
                    if (ReferenceEquals(m_Socket, ws)) ws.Abort();
                }
                return false;
            }
            finally
            {
                lock (m_Gate)
                {
                    m_Sends--;
                    DisposeStopIfIdle();
                }
            }
        }

        public void Dispose()
        {
            lock (m_Gate)
            {
                if (m_Disposed) return;
                m_Disposed = true;
                m_Connected = false;
                m_Stop.Cancel();
                m_Socket?.Abort();
                DisposeStopIfIdle();
            }
        }

        // Called with m_Gate held. No thread may use the source while it is disposed.
        void DisposeStopIfIdle()
        {
            if (!m_Disposed || !m_RunFinished || m_Sends != 0 || m_StopDisposed) return;
            m_StopDisposed = true;
            m_Stop.Dispose();
        }
    }
}
