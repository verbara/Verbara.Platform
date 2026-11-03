using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core;

/// <summary>
/// What the inbound pipeline did with one provider message. <see cref="IsDuplicate"/> is <c>true</c> when the
/// provider message id was already stored for the tenant (a replay, or the loser of a concurrent identical
/// delivery): nothing new was written, the ids name the stored message, and the caller MUST NOT repeat any side
/// effect of the first delivery — no event, no routing, no queue assignment, no bot turn. The one exception is
/// <see cref="ClosedOnReturnId"/>.
/// </summary>
/// <param name="ConversationId">The conversation the message is stored in.</param>
/// <param name="ContactId">The contact the message came from.</param>
/// <param name="MessageId">The stored message.</param>
/// <param name="IsNewConversation">Whether this delivery opened the conversation.</param>
/// <param name="IsDuplicate">Whether the message was already stored (a replay, or the loser of the identical-delivery insert).</param>
/// <param name="ClosedOnReturnId">
/// conversations-close-after-inactivity (design D8): the <c>Resolved</c> conversation this delivery closed with
/// <c>customer-returned</c> (it won <c>TryCloseResolvedForReplyAsync</c>), for the caller to announce once; null when
/// it lost that close, when nothing was <c>Resolved</c>, and on a replay. A delivery that won the close and then lost
/// the identical-delivery insert keeps it alongside <see cref="IsDuplicate"/>: it is the only delivery that owns the
/// close, and the only side effect a duplicate result may carry.
/// </param>
public sealed record PipelineResult(
    EntityId ConversationId,
    EntityId ContactId,
    EntityId MessageId,
    bool IsNewConversation,
    bool IsDuplicate = false,
    EntityId? ClosedOnReturnId = null);
