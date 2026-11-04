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
        (ConversationState.Resolved, ConversationState.Active),  // no caller; see D8 (conversations-close-after-inactivity)
        (ConversationState.Resolved, ConversationState.Closed),

        // From Escalated
        (ConversationState.Escalated, ConversationState.Active),
        (ConversationState.Escalated, ConversationState.Queued),
    ];

    /// <summary>
    /// The terminal states: <c>Closed</c>, <c>Abandoned</c>, <c>Merged</c>, <c>Spam</c>. Read by the stores that bind
    /// them in SQL, so the set is stated once (contact-history-opens-by-assignment, design D2).
    /// </summary>
    public static readonly IReadOnlyList<ConversationState> TerminalStates =
    [
        ConversationState.Closed,
        ConversationState.Abandoned,
        ConversationState.Merged,
        ConversationState.Spam,
    ];

    private static readonly HashSet<ConversationState> s_terminalStates = [.. TerminalStates];

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

    /// <summary>
    /// conversations-close-after-inactivity (design D3): the states the inactivity close may close directly —
    /// agent-held (<c>Active</c>, <c>OnHold</c>, <c>Consulting</c>) and parked (<c>WaitingForCustomer</c>,
    /// <c>Resolved</c>). A separate set, not an edge of the transition table: <see cref="CanTransition"/> still refuses
    /// <c>Active/OnHold/Consulting → Closed</c>, so the generic closes (supervisor force-close, automation, the bot's
    /// <c>EndConversation</c>) keep refusing an agent-held conversation. Read only by the inactivity close (the
    /// switchboard and the stores' conditional close), never by <see cref="Conversation.TransitionTo"/>.
    /// </summary>
    public static readonly IReadOnlyList<ConversationState> InactivityCloseStates =
    [
        ConversationState.Active,
        ConversationState.OnHold,
        ConversationState.Consulting,
        ConversationState.WaitingForCustomer,
        ConversationState.Resolved,
    ];

    private static readonly HashSet<ConversationState> s_inactivityCloseStates = [.. InactivityCloseStates];

    /// <summary>Whether the inactivity close may close a conversation read in <paramref name="from"/> (design D3).</summary>
    public static bool CanCloseForInactivity(ConversationState from) => s_inactivityCloseStates.Contains(from);

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
