using System.Threading;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.WebChat;

public sealed class WebChatSession
{
    private int _routed;

    public required string SessionId { get; init; }
    public required EntityId ConversationId { get; init; }
    public required TenantId TenantId { get; init; }
    public required ChannelAddress CustomerAddress { get; init; }
    public bool IsConnected { get; set; } = true;
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Atomically marks this session as routed-to-queue. Returns <c>true</c> exactly once (the
    /// first call) and <c>false</c> on every subsequent call, so only the first inbound message
    /// of a session triggers queue assignment.
    /// </summary>
    public bool TryMarkRouted() => Interlocked.CompareExchange(ref _routed, 1, 0) == 0;
}
