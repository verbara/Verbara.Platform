using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.WebChat;

public sealed class WebChatSession
{
    public required string SessionId { get; init; }
    public required EntityId ConversationId { get; init; }
    public required TenantId TenantId { get; init; }
    public required ChannelAddress CustomerAddress { get; init; }
    public bool IsConnected { get; set; } = true;
    public DateTimeOffset LastActivityAt { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
}
