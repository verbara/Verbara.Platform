using Asterisk.Platform.Conversations;

namespace Asterisk.Platform.Channels.WebChat;

/// <summary>
/// Server to Client WebSocket message envelope.
/// </summary>
public sealed record WebChatWsMessage(string Type, MessageEnvelope? Data);

/// <summary>
/// Client to Server WebSocket message (text only).
/// </summary>
public sealed record WebChatClientMessage(string Type, string? Text);
