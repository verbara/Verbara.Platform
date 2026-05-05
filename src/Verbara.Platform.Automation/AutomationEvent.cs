using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Automation;

public sealed class AutomationEvent
{
    public required AutomationTrigger Trigger { get; init; }
    public required TenantId TenantId { get; init; }
    public required EntityId ConversationId { get; init; }
    public EntityId? ContactId { get; init; }
    public ChannelType? Channel { get; init; }
    public ConversationState? State { get; init; }
    public Message? Message { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}
