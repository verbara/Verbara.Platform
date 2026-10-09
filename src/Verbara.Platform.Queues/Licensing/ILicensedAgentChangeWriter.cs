using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Licensing;

/// <summary>Which ownership call site moved a conversation to an agent.</summary>
public enum OwnershipChangeKind
{
    /// <summary>A supervisor took the conversation over with its own agent.</summary>
    TakenOver,

    /// <summary>The owner (or a supervisor on its behalf) transferred it to an agent.</summary>
    Transferred,

    /// <summary>A supervisor reassigned it to an agent.</summary>
    Reassigned,
}

/// <summary>An ownership change the switchboard commits together with its ledger row: the call site and its actor.</summary>
public sealed record OwnershipChange(OwnershipChangeKind Kind, string ActorUserId)
{
    /// <summary>The ledger kind this change is recorded as.</summary>
    public string LedgerKind => Kind switch
    {
        OwnershipChangeKind.TakenOver => LicenseAgentEventKinds.ConversationTakenOver,
        OwnershipChangeKind.Transferred => LicenseAgentEventKinds.ConversationTransferred,
        OwnershipChangeKind.Reassigned => LicenseAgentEventKinds.ConversationReassigned,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown ownership change kind."),
    };
}

/// <summary>
/// Commits a conversation's move to an agent together with its <c>license_agent_events</c> row, in one
/// transaction (licensed-agent-ledger, design D5). The switchboard depends on this half only.
/// </summary>
public interface ILicensedAgentOwnershipWriter
{
    /// <summary>
    /// Saves <paramref name="conversation"/>, whose owner is already the receiving agent, and appends the
    /// <see cref="OwnershipChange.LedgerKind"/> row for it. Either both commit or neither does.
    /// </summary>
    Task CommitOwnershipAsync(Conversation conversation, OwnershipChange change, CancellationToken ct);
}

/// <summary>
/// The single write path for the changes that can alter who counts as a licensed agent
/// (licensed-agent-ledger, design D5): each method writes the domain row and its ledger row in one
/// transaction, or neither. Agent creation and deletion live here; the user changes are declared on
/// <c>Verbara.Platform.Identity.ILicensedUserChangeWriter</c>, because Queues does not reference Identity.
/// Both are implemented by the same writer in each storage mode.
/// </summary>
/// <remarks>
/// The realtime side effects of a change (PJSIP upsert or removal) are not run here: the caller runs them
/// after the commit, so a rolled-back change never touches Asterisk.
/// </remarks>
public interface ILicensedAgentChangeWriter : ILicensedAgentOwnershipWriter
{
    /// <summary>
    /// Inserts <paramref name="agent"/> and appends <c>agent_created</c>. A second agent for the same user is
    /// refused with <see cref="EntityAlreadyExistsException"/>, and nothing is written.
    /// </summary>
    Task CommitAgentCreatedAsync(Agent agent, string? actorUserId, CancellationToken ct);

    /// <summary>Deletes the agent and appends <c>agent_deleted</c>. Returns false (and writes nothing) when there is no such agent.</summary>
    Task<bool> CommitAgentDeletedAsync(TenantId tenantId, EntityId agentId, string? actorUserId, CancellationToken ct);
}
