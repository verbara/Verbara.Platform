using System.Net.WebSockets;
using System.Text.Json;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Channels.WebChat;

/// <summary>
/// The in-process registry of visitor sockets. A session can hold several sockets at once (a reload
/// re-attaching before the old socket's cleanup ran, or two tabs sharing the session); a reply goes to
/// every open one, and removing a socket removes only that socket. Sends on one socket are serialized,
/// because a <see cref="WebSocket"/> allows a single outstanding send.
/// </summary>
public sealed class WebSocketWebChatTransport : IWebChatTransport
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Connection>> _connections = new(StringComparer.Ordinal);

    public async Task<bool> SendToClientAsync(string sessionId, MessageEnvelope message, CancellationToken ct)
    {
        var connections = Snapshot(sessionId);
        if (connections.Length == 0)
            return false;

        var json = JsonSerializer.SerializeToUtf8Bytes(
            new WebChatWsMessage("message", message),
            WebChatJsonContext.Default.WebChatWsMessage);

        var delivered = false;
        foreach (var connection in connections)
            delivered |= await connection.TrySendAsync(json, ct).ConfigureAwait(false);
        return delivered;
    }

    public async Task DisconnectAsync(string sessionId, CancellationToken ct)
    {
        Connection[] removed;
        lock (_gate)
        {
            if (!_connections.Remove(sessionId, out var list))
                return;
            removed = [.. list];
        }

        foreach (var connection in removed.Where(c => c.Socket.State == WebSocketState.Open))
            await connection.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", ct).ConfigureAwait(false);
    }

    /// <summary>Called by the WebSocket endpoint when a visitor socket attaches to a session.</summary>
    public void Register(string sessionId, WebSocket ws)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(sessionId, out var list))
                _connections[sessionId] = list = [];
            list.Add(new Connection(ws));
        }
    }

    /// <summary>
    /// Called when a visitor socket ends: removes that socket only, so the session's other sockets keep
    /// receiving replies.
    /// </summary>
    public void Unregister(string sessionId, WebSocket ws)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(sessionId, out var list))
                return;
            list.RemoveAll(c => ReferenceEquals(c.Socket, ws));
            if (list.Count == 0)
                _connections.Remove(sessionId);
        }
    }

    private Connection[] Snapshot(string sessionId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue(sessionId, out var list) ? [.. list] : [];
        }
    }

    private sealed class Connection(WebSocket socket)
    {
        private readonly Lock _sendGate = new();
        private Task _tail = Task.CompletedTask;

        public WebSocket Socket { get; } = socket;

        /// <summary>
        /// Queues one frame behind the socket's previous send; <c>false</c> when the socket is not open or
        /// the send failed.
        /// </summary>
        public Task<bool> TrySendAsync(byte[] payload, CancellationToken ct)
        {
            lock (_sendGate)
            {
                var send = _tail.ContinueWith(
                    _ => SendCoreAsync(payload, ct),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default).Unwrap();
                _tail = send;
                return send;
            }
        }

        private async Task<bool> SendCoreAsync(byte[] payload, CancellationToken ct)
        {
            if (Socket.State != WebSocketState.Open)
                return false;
            try
            {
                await Socket.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
            {
                // The socket closed between the state check and the send (ObjectDisposedException is an
                // InvalidOperationException): nothing reached the visitor through this socket.
                return false;
            }
        }
    }
}
