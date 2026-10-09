namespace Verbara.Platform.Queues.Licensing;

/// <summary>
/// The kinds of a <c>license_agent_events</c> row (licensed-agent-ledger), exactly as frozen by
/// <c>fixtures/licensed-agent-export.v1.json</c>.
/// </summary>
public static class LicenseAgentEventKinds
{
    public const string ChainAnchored = "chain_anchored";
    public const string ChainReanchored = "chain_reanchored";
    public const string AgentBaseline = "agent_baseline";
    public const string AgentCreated = "agent_created";
    public const string AgentDeleted = "agent_deleted";
    public const string UserStatusChanged = "user_status_changed";
    public const string UserDeleted = "user_deleted";
    public const string ConversationTakenOver = "conversation_taken_over";
    public const string ConversationTransferred = "conversation_transferred";
    public const string ConversationReassigned = "conversation_reassigned";

    /// <summary>A takeover, transfer or reassign: it adds its receiver to the count, never removes anyone.</summary>
    public static bool IsOwnership(string kind) =>
        kind is ConversationTakenOver or ConversationTransferred or ConversationReassigned;

    /// <summary>A row that carries no agent: the chain's anchor or a licence re-anchor.</summary>
    public static bool IsChainRow(string kind) => kind is ChainAnchored or ChainReanchored;
}

/// <summary>
/// One <c>license_agent_events</c> row: a change that can alter who counts as a licensed agent, chained to
/// its predecessor (licensed-agent-ledger, design D6). Ids only; no names, e-mails or content.
/// </summary>
public sealed record LicenseAgentEvent
{
    /// <summary>The tenant whose chain this row belongs to; null for the deployment chain.</summary>
    public required string? TenantId { get; init; }

    /// <summary>Gap-free position in the chain, shared with the chain's daily rows.</summary>
    public required long Sequence { get; init; }

    public required Guid EventId { get; init; }

    /// <summary>One of <see cref="LicenseAgentEventKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>When the change was committed, UTC, microsecond precision (<see cref="LicenseAgentChain.Normalize"/>).</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    public required string? AgentId { get; init; }
    public required string? UserId { get; init; }
    public required string? ActorUserId { get; init; }
    public required string? ConversationId { get; init; }

    /// <summary><c>Active</c>, <c>Suspended</c> or <c>Deactivated</c> on a user row; null otherwise.</summary>
    public required string? UserStatus { get; init; }

    /// <summary>The agent's countability after the change (agent row exists AND its user is Active); null on chain rows.</summary>
    public required bool? Counted { get; init; }

    public required string? LicenseId { get; init; }
    public required string PrevHash { get; init; }
    public required string RowHash { get; init; }
}

/// <summary>
/// One <c>license_agent_daily</c> row: a closed day's simultaneous peak for a Customer tenant, or the
/// deployment total when <see cref="TenantId"/> is null (licensed-agent-daily-close).
/// </summary>
public sealed record LicenseAgentDaily
{
    public required string? TenantId { get; init; }
    public required long Sequence { get; init; }

    /// <summary>The calendar day in the deployment's day zone.</summary>
    public required DateOnly Day { get; init; }

    /// <summary>0 for the close; each correction appends the next revision. Readers take the highest.</summary>
    public required int Revision { get; init; }

    public required int LicensedAgents { get; init; }
    public required DateTimeOffset ClosedAt { get; init; }

    /// <summary>The chain's head sequence the close had seen when it wrote this row.</summary>
    public required long ClosedThroughSequence { get; init; }

    public required string? LicenseId { get; init; }
    public required string PrevHash { get; init; }
    public required string RowHash { get; init; }
}
