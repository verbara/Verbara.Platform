using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

/// <summary>
/// What the inbound pipeline did with one provider message. <see cref="IsDuplicate"/> is <c>true</c> when the
/// provider message id was already stored for the tenant (a replay, or the loser of a concurrent identical
/// delivery): nothing new was written, the ids name the stored message, and the caller MUST NOT repeat any side
/// effect of the first delivery — no event, no routing, no queue assignment, no bot turn.
/// </summary>
public sealed record PipelineResult(
    EntityId ConversationId,
    EntityId ContactId,
    EntityId MessageId,
    bool IsNewConversation,
    bool IsDuplicate = false);
