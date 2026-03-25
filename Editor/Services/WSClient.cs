using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Timers;
using BeziWebSocketSharp;
using UnityEngine;

namespace CoBuddy.Editor.Services
{
    /// <summary>
    /// WebSocket client that connects to the CoBuddy Electron app's WS server.
    /// The Electron app is the server (always running), the Unity plugin is the client
    /// (reconnects after domain reloads). Modelled after a reference app pattern.
    /// </summary>
    public class WSClient
    {
        private WebSocket _ws;
        private string _projectPath = "";
        private readonly Queue<string> _messageQueue = new Queue<string>();
        private System.Timers.Timer _pingTimer;
        private bool _waitingForPong;
        private const int PingIntervalMs = 5000;
        private bool _isPaused;
        private bool _isDisposed;

        public Action<string> OnRpcRequest; // incoming RPC request from app

        /// <summary>Socket error codes that are expected during normal operation (domain reload, etc.)</summary>
        private static readonly SocketError[] IgnoredSocketErrors = new SocketError[]
        {
            SocketError.NetworkDown,
            SocketError.NetworkUnreachable,
            SocketError.NetworkReset,
            SocketError.ConnectionReset,
            SocketError.ConnectionAborted,
            SocketError.ConnectionRefused,
            SocketError.HostDown,
            SocketError.HostUnreachable,
            SocketError.WouldBlock,
            SocketError.InProgress,
            SocketError.Interrupted,
            SocketError.NotConnected,
            SocketError.Disconnecting,
            SocketError.Shutdown,
            SocketError.TimedOut,
        };

        public enum Status
        {
            Disconnected,
            Connecting,
            Connected,
        }

        public Status CurrentStatus
        {
            get
            {
                if (_ws == null) return Status.Disconnected;
                if (_ws.ReadyState == WebSocketState.Open) return Status.Connected;
                if (_ws.ReadyState == WebSocketState.Connecting) return Status.Connecting;
                return Status.Disconnected;
            }
        }

        public bool IsConnected => CurrentStatus == Status.Connected;

        public WSClient(string projectPath)
        {
            _projectPath = projectPath ?? "";
        }

        public void UpdateProjectPath(string projectPath)
        {
            _projectPath = projectPath ?? "";
        }

        /// <summary>Pause before domain reload — disconnects cleanly.</summary>
        public void Pause()
        {
            _isPaused = true;
            Disconnect();
        }

        /// <summary>Resume after domain reload — reconnects.</summary>
        public void Resume()
        {
            _isPaused = false;
            Connect();
        }

        public void Connect()
        {
            if (_isPaused || _isDisposed) return;

            // Already connecting or connected
            if (_ws != null && (_ws.ReadyState == WebSocketState.Open ||
                                _ws.ReadyState == WebSocketState.Connecting))
                return;

            Disconnect();

            try
            {
                string encodedPath = Uri.EscapeDataString(_projectPath);
                string url = $"ws://127.0.0.1:38473?projectPath={encodedPath}";

                _ws = new WebSocket(url);
                _ws.OnOpen += OnOpen;
                _ws.OnMessage += OnMessage;
                _ws.OnClose += OnClose;
                _ws.OnError += OnError;
                _ws.ConnectAsync();
            }
            catch (Exception ex)
            {
                if (!IsThreadAbortedByDomainReload(ex))
                    Debug.LogWarning($"[CoBuddy] WSClient connect error: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            _waitingForPong = false;
            _pingTimer?.Dispose();
            _pingTimer = null;

            if (_ws != null)
            {
                _ws.OnOpen -= OnOpen;
                _ws.OnMessage -= OnMessage;
                _ws.OnClose -= OnClose;
                _ws.OnError -= OnError;
                try { _ws.CloseAsync(); } catch { }
                try { ((IDisposable)_ws).Dispose(); } catch { }
                _ws = null;
            }
        }

        public void Dispose()
        {
            _isDisposed = true;
            Disconnect();
            lock (_messageQueue) { _messageQueue.Clear(); }
        }

        // ── Event handlers ───────────────────────────────────────

        private void OnOpen(object sender, EventArgs e)
        {
            Debug.Log("[CoBuddy] WebSocket connected to app");
            StartPinging();

            // Flush queued messages
            lock (_messageQueue)
            {
                while (_messageQueue.Count > 0)
                {
                    if (_ws != null && _ws.ReadyState == WebSocketState.Open)
                    {
                        string msg = _messageQueue.Dequeue();
                        try { _ws.Send(msg); } catch { break; }
                    }
                    else break;
                }
            }
        }

        private void OnMessage(object sender, MessageEventArgs e)
        {
            try
            {
                string data = e.Data;
                if (string.IsNullOrEmpty(data)) return;

                // Check for Pong response
                if (data.Contains("\"name\":\"Pong\"") || data.Contains("\"name\": \"Pong\""))
                {
                    _waitingForPong = false;
                    return;
                }

                // Forward RPC requests to handler
                OnRpcRequest?.Invoke(data);
            }
            catch (Exception ex)
            {
                if (!IsThreadAbortedByDomainReload(ex))
                    Debug.LogWarning($"[CoBuddy] WSClient message error: {ex.Message}");
            }
        }

        private async void OnClose(object sender, CloseEventArgs e)
        {
            if (_isPaused || _isDisposed) return;
            // Reconnect after 1 second (reduced from 3s for faster recovery)
            await System.Threading.Tasks.Task.Delay(1000);
            if (!_isPaused && !_isDisposed)
                Connect();
        }

        private void OnError(object sender, BeziWebSocketSharp.ErrorEventArgs e)
        {
            var ex = e.Exception;
            if (ex == null || IsThreadAbortedByDomainReload(ex)) return;

            // Ignore expected socket errors
            if (ex is IOException ioEx && ioEx.InnerException is SocketException sockEx)
            {
                foreach (var ignored in IgnoredSocketErrors)
                {
                    if (sockEx.SocketErrorCode == ignored) return;
                }
            }

            // Don't log connection refused — it just means the app isn't running yet
            if (ex.Message != null && ex.Message.Contains("refused"))
                return;

            Debug.LogWarning($"[CoBuddy] WSClient error: {ex.Message}");
        }

        // ── Sending messages ─────────────────────────────────────

        /// <summary>Send a push event to the app (e.g., compileFinish, selectionChanged).</summary>
        public void SendEvent(string eventName, string payloadJson)
        {
            string msg = $"{{\"type\":\"event\",\"name\":\"{BridgeServer.EscapeJson(eventName)}\",\"payload\":{payloadJson}}}";
            SendRaw(msg);
        }

        /// <summary>Send a response to an RPC request from the app.</summary>
        public void SendResponse(string id, string resultJson)
        {
            string msg = $"{{\"type\":\"response\",\"id\":\"{BridgeServer.EscapeJson(id ?? "")}\",\"result\":{resultJson}}}";
            SendRaw(msg);
        }

        private void SendRaw(string message)
        {
            try
            {
                if (_ws != null && _ws.ReadyState == WebSocketState.Open)
                {
                    try
                    {
                        _ws.Send(message);
                        return;
                    }
                    catch (InvalidOperationException ex)
                    {
                        if (ex.Message.Contains("not Open"))
                        {
                            // Socket transitioned to non-Open mid-send — queue and reconnect
                            if (!message.Contains("\"name\":\"Ping\""))
                                lock (_messageQueue) { _messageQueue.Enqueue(message); }
                            Disconnect();
                            Connect();
                            return;
                        }
                        throw;
                    }
                }

                if (_ws != null && _ws.ReadyState == WebSocketState.Connecting)
                {
                    if (!message.Contains("\"name\":\"Ping\""))
                        lock (_messageQueue) { _messageQueue.Enqueue(message); }
                    return;
                }

                // Not connected — queue and try to reconnect
                if (!message.Contains("\"name\":\"Ping\""))
                    lock (_messageQueue) { _messageQueue.Enqueue(message); }
                Disconnect();
                Connect();
            }
            catch (Exception ex)
            {
                if (!IsThreadAbortedByDomainReload(ex))
                    Debug.LogWarning($"[CoBuddy] WSClient send error: {ex.Message}");
            }
        }

        // ── Ping/keepalive ───────────────────────────────────────

        private void StartPinging()
        {
            _pingTimer?.Dispose();
            var timer = new System.Timers.Timer(PingIntervalMs);
            timer.Elapsed += (s, e) => PingServer();
            timer.AutoReset = true;
            timer.Enabled = true;
            _pingTimer = timer;
        }

        private void PingServer()
        {
            try
            {
                if (_waitingForPong)
                {
                    // No pong received — reconnect
                    Connect();
                    return;
                }
                _waitingForPong = true;
                SendRaw("{\"type\":\"event\",\"name\":\"Ping\",\"payload\":{}}");
            }
            catch (Exception ex)
            {
                if (!IsThreadAbortedByDomainReload(ex))
                    Debug.LogWarning($"[CoBuddy] WSClient ping error: {ex.Message}");
                Connect();
            }
        }

        // ── Helpers ──────────────────────────────────────────────

        /// <summary>Check if an exception was caused by Unity domain reload (thread abort).</summary>
        public static bool IsThreadAbortedByDomainReload(Exception ex)
        {
            if (ex is ThreadAbortException) return true;
            if (ex is System.Threading.Tasks.TaskCanceledException) return true;
            if (ex?.InnerException is ThreadAbortException) return true;
            return false;
        }
    }
}
