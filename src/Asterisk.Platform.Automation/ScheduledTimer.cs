using Asterisk.Platform.Core;

namespace Asterisk.Platform.Automation;

public sealed class ScheduledTimer
{
    public required EntityId TimerId { get; init; }
    public required TenantId TenantId { get; init; }
    public required EntityId ConversationId { get; init; }
    public required EntityId CallbackRuleId { get; init; }
    public required DateTimeOffset FireAt { get; init; }
    public bool IsFired { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
}
