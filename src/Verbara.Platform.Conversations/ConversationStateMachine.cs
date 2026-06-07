namespace Verbara.Platform.Conversations;

public static class ConversationStateMachine
{
    private static readonly HashSet<(ConversationState From, ConversationState To)> s_validTransitions =
    [
        // From Queued
        (ConversationState.Queued, ConversationState.Offered),
        (ConversationState.Queued, ConversationState.Abandoned),

        // From Offered
        (ConversationState.Offered, ConversationState.Active),
        (ConversationState.Offered, ConversationState.Queued), // agent rejects

        // From Active
        (ConversationState.Active, ConversationState.OnHold),
        (ConversationState.Active, ConversationState.Consulting),
        (ConversationState.Active, ConversationState.WrapUp),
        (ConversationState.Active, ConversationState.WaitingForCustomer),
        (ConversationState.Active, ConversationState.Escalated),
        (ConversationState.Active, ConversationState.Merged),
        (ConversationState.Active, ConversationState.Spam),

        // From OnHold
        (ConversationState.OnHold, ConversationState.Active),

        // From Consulting
        (ConversationState.Consulting, ConversationState.Active),

        // From WrapUp
        (ConversationState.WrapUp, ConversationState.Resolved),
        (ConversationState.WrapUp, ConversationState.Closed),

        // From WaitingForCustomer
        (ConversationState.WaitingForCustomer, ConversationState.Active),
        (ConversationState.WaitingForCustomer, ConversationState.Snoozed),
        (ConversationState.WaitingForCustomer, ConversationState.Closed),

        // From Snoozed
        (ConversationState.Snoozed, ConversationState.Active),
        (ConversationState.Snoozed, ConversationState.Queued),

        // From Resolved
        (ConversationState.Resolved, ConversationState.Active),  // customer replies
        (ConversationState.Resolved, ConversationState.Closed),

        // From Escalated
        (ConversationState.Escalated, ConversationState.Active),
        (ConversationState.Escalated, ConversationState.Queued),
    ];

    private static readonly HashSet<ConversationState> s_terminalStates =
    [
        ConversationState.Closed,
        ConversationState.Abandoned,
        ConversationState.Merged,
        ConversationState.Spam,
    ];

    /// <summary>W4 — conversation states that count as the agent actively working an item
    /// (block a deferred pause from applying). Excludes parked + pre-accept + terminal.</summary>
    public static readonly IReadOnlyList<ConversationState> ActiveWorkStates =
        [ConversationState.Active, ConversationState.OnHold, ConversationState.Consulting, ConversationState.WrapUp];

    public static bool IsActiveWork(ConversationState state) => ActiveWorkStates.Contains(state);

    /// <summary>W5 — conversation states a work-failover re-queue applies to: a LIVE
    /// customer is engaged/waiting. Excludes WrapUp (no live customer; closed by the
    /// wrap-up timeout) and parked/pre-accept. Narrower than ActiveWorkStates.</summary>
    public static readonly IReadOnlyList<ConversationState> FailoverWorkStates =
        [ConversationState.Active, ConversationState.OnHold, ConversationState.Consulting];

    public static bool IsFailoverWork(ConversationState state) => FailoverWorkStates.Contains(state);

    public static bool CanTransition(ConversationState from, ConversationState to) =>
        s_validTransitions.Contains((from, to));

    public static bool IsTerminal(ConversationState state) =>
        s_terminalStates.Contains(state);

    public static void EnsureTransition(ConversationState from, ConversationState to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException(
                $"Invalid conversation state transition: {from} → {to}");
    }
}
