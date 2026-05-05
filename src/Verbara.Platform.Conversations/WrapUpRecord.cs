using Verbara.Platform.Core;

namespace Verbara.Platform.Conversations;

public sealed class WrapUpRecord
{
    public required TenantId TenantId { get; init; }
    public required EntityId ConversationId { get; init; }
    public required EntityId AgentId { get; init; }
    public required EntityId DispositionId { get; init; }
    public string? Notes { get; init; }
    public required TimeSpan Duration { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}
