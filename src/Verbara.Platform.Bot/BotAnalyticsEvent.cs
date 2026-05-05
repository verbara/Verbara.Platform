using Verbara.Platform.Core;

namespace Verbara.Platform.Bot;

/// <summary>
/// An analytics event emitted by the bot orchestrator at significant points in a conversation lifecycle.
/// </summary>
/// <param name="BotId">The bot that processed the conversation.</param>
/// <param name="TenantId">The tenant owning the conversation.</param>
/// <param name="ConversationId">The conversation this event belongs to.</param>
/// <param name="Type">What happened.</param>
/// <param name="OccurredAt">When this event occurred (UTC).</param>
/// <param name="TurnCount">The number of turns at the time of the event (optional).</param>
/// <param name="HandoffReason">The reason for handoff when applicable (optional).</param>
public sealed record BotAnalyticsEvent(
    EntityId BotId,
    TenantId TenantId,
    EntityId ConversationId,
    BotAnalyticsEventType Type,
    DateTimeOffset OccurredAt,
    int? TurnCount = null,
    string? HandoffReason = null);

/// <summary>
/// Classifies the kind of <see cref="BotAnalyticsEvent"/> emitted by the orchestrator.
/// </summary>
public enum BotAnalyticsEventType
{
    /// <summary>The bot started handling a new conversation.</summary>
    ConversationStarted = 0,

    /// <summary>The bot resolved the conversation without a human handoff.</summary>
    Resolved = 1,

    /// <summary>The bot handed the conversation off to a queue.</summary>
    HandedOff = 2,

    /// <summary>The bot encountered an unrecoverable error.</summary>
    Failed = 3,

    /// <summary>The bot reached the maximum allowed turn count and forced a handoff.</summary>
    MaxTurnsReached = 4,
}
