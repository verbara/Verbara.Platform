using Verbara.Platform.Core;

namespace Verbara.Platform.Bot;

/// <summary>
/// A persisted record of a bot analytics event for aggregation and reporting.
/// </summary>
public sealed class BotAnalyticsRecord
{
    public required string EventType { get; init; }
    public required string BotId { get; init; }
    public required string ConversationId { get; init; }
    public int TurnCount { get; init; }
    public string? HandoffReason { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
