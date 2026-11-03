namespace Verbara.Platform.Conversations;

/// <summary>
/// conversations-close-after-inactivity (design D6, D12): the reason a system close records in the conversation's
/// metadata under <see cref="MetadataKey"/>, in the audit entry's <c>reason</c> and on the close counter. The
/// underscore marks a system key, as <c>_offeredAt</c>. A close by a person records none.
/// </summary>
public static class ConversationCloseReasons
{
    /// <summary>The metadata key of the reason.</summary>
    public const string MetadataKey = "_closedReason";

    /// <summary>The inactivity close of a conversation idle for its window (and not for longer than window + grace).</summary>
    public const string Inactivity = "inactivity";

    /// <summary>The inactivity close of a conversation idle for longer than its window plus the grace: a catch-up wave (D12).</summary>
    public const string InactivityBacklog = "inactivity-backlog";

    /// <summary>The close of a <c>Resolved</c> conversation the customer wrote to again (D8).</summary>
    public const string CustomerReturned = "customer-returned";
}

/// <summary>
/// What the inactivity pass read for one idle conversation, and the cut-offs of its window (design D4, D12). The
/// conditional close re-checks the state, the owner and the clock against these, so a change committed since the read
/// makes it match nothing.
/// </summary>
/// <param name="ConversationId">The conversation read.</param>
/// <param name="ReadState">Its state at the read; must pass <see cref="ConversationStateMachine.CanCloseForInactivity"/>.</param>
/// <param name="ReadOwner">Its owner at the read (compared kind and id, null-safe).</param>
/// <param name="Cutoff"><c>now - window</c>: the clock must still be older.</param>
/// <param name="BacklogCutoff"><c>now - window - grace</c>: a clock older than this records <see cref="ConversationCloseReasons.InactivityBacklog"/>.</param>
/// <param name="Now">The close time (<c>closed_at</c>, <c>updated_at</c>).</param>
public sealed record InactivityCloseRequest(
    Core.EntityId ConversationId,
    ConversationState ReadState,
    ConversationOwner? ReadOwner,
    DateTimeOffset Cutoff,
    DateTimeOffset BacklogCutoff,
    DateTimeOffset Now);

/// <summary>A won inactivity close: the reason the statement decided, and the clock it closed on.</summary>
public sealed record InactivityCloseOutcome(string Reason, DateTimeOffset LastMessageAt);
