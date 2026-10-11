using System.Threading;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WebChat;

public sealed class WebChatSession
{
    private readonly Lock _socketGate = new();
    private int _routed;
    private int _attachedSockets;

    public required string SessionId { get; init; }

    /// <summary>
    /// The conversation the visitor's messages currently land in: the one the session was created with,
    /// until it ends and a visitor message opens a new one for the same contact (spec
    /// <c>webchat-visitor-identity</c>).
    /// </summary>
    public required EntityId ConversationId { get; set; }

    public required TenantId TenantId { get; init; }
    public required ChannelAddress CustomerAddress { get; init; }
    public bool IsConnected { get; set; } = true;
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>How many visitor sockets are attached to this session right now.</summary>
    public int AttachedSockets
    {
        get
        {
            lock (_socketGate)
                return _attachedSockets;
        }
    }

    /// <summary>
    /// Atomically marks this session as routed-to-queue. Returns <c>true</c> exactly once (the
    /// first call) and <c>false</c> on every subsequent call, so only the first inbound message
    /// of a session triggers queue assignment.
    /// </summary>
    public bool TryMarkRouted() => Interlocked.CompareExchange(ref _routed, 1, 0) == 0;

    /// <summary>A visitor socket attached: the session is connected while any socket is.</summary>
    internal void AttachSocket(DateTimeOffset now)
    {
        lock (_socketGate)
        {
            _attachedSockets++;
            IsConnected = true;
            LastActivityAt = now;
        }
    }

    /// <summary>
    /// A visitor socket ended: the session disconnects only when it was the last one. A detach with no
    /// socket attached is a no-op, so it never disconnects a session that has not attached yet.
    /// </summary>
    internal void DetachSocket()
    {
        lock (_socketGate)
        {
            if (_attachedSockets == 0)
                return;
            _attachedSockets--;
            if (_attachedSockets == 0)
                IsConnected = false;
        }
    }
}
