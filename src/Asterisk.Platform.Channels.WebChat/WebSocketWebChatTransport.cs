using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Channels.WebChat;

public sealed class WebSocketWebChatTransport : IWebChatTransport
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();

    public async Task SendToClientAsync(string sessionId, MessageEnvelope message, CancellationToken ct)
    {
        if (!_connections.TryGetValue(sessionId, out var ws) || ws.State != WebSocketState.Open)
            return;

        var json = JsonSerializer.SerializeToUtf8Bytes(
            new WebChatWsMessage("message", message),
            WebChatJsonContext.Default.WebChatWsMessage);
        await ws.SendAsync(json, WebSocketMessageType.Text, true, ct);
    }

    public async Task DisconnectAsync(string sessionId, CancellationToken ct)
    {
        if (_connections.TryRemove(sessionId, out var ws) && ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", ct);
    }

    /// <summary>Called by WebSocket middleware to register a client connection.</summary>
    public void Register(string sessionId, WebSocket ws) => _connections[sessionId] = ws;

    /// <summary>Called when a client disconnects.</summary>
    public void Unregister(string sessionId) => _connections.TryRemove(sessionId, out _);
}
