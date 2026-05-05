using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core.Pipeline;

public sealed class PipelineContext
{
    public required InboundMessage InboundMessage { get; init; }
    public required TenantId TenantId { get; init; }
    public required ChannelType Channel { get; init; }
    public Contact? Contact { get; set; }
    public Conversation? Conversation { get; set; }
    public Message? PersistedMessage { get; set; }
    public bool IsDuplicate { get; set; }
    public bool IsNewConversation { get; set; }
}
