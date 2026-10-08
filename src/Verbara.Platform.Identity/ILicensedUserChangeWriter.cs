using Verbara.Platform.Core;

namespace Verbara.Platform.Identity;

/// <summary>What <see cref="ILicensedUserChangeWriter.CommitUserDeletedAsync"/> did.</summary>
/// <param name="UserDeleted">The user row was deleted.</param>
/// <param name="OwnsAgent">Nothing was written because the user owns an agent and the caller did not ask to delete it.</param>
/// <param name="DeletedAgentId">The agent deleted with the user, when the caller asked for it and there was one.</param>
public sealed record LicensedUserDeletion(bool UserDeleted, bool OwnsAgent, string? DeletedAgentId)
{
    public static LicensedUserDeletion NotFound { get; } = new(false, false, null);
    public static LicensedUserDeletion RefusedOwnsAgent { get; } = new(false, true, null);
}

/// <summary>
/// The user half of the licensed-agent write path (licensed-agent-ledger, design D5): a status change or a
/// deletion of a user who owns an agent commits with its <c>license_agent_events</c> row, in one transaction.
/// A user who owns no agent gets no ledger row. Implemented by the same writer as
/// <c>Verbara.Platform.Queues.Licensing.ILicensedAgentChangeWriter</c>.
/// </summary>
public interface ILicensedUserChangeWriter
{
    /// <summary>
    /// <see cref="IUserStore.UpdateAdminFieldsAsync"/>, plus a <c>user_status_changed</c> row when the status
    /// written differs from the one replaced and the user owns an agent. Same outcomes as the store method.
    /// </summary>
    Task<AdminFieldsWriteResult> CommitUserStatusChangedAsync(
        TenantId tenantId, EntityId userId, AdminFieldsChange change, DateTimeOffset updatedAt, string? updatedBy,
        CancellationToken ct);

    /// <summary>
    /// Deletes the user. When it owns an agent: with <paramref name="deleteOwnedAgent"/> the agent is deleted
    /// first in the same transaction (<c>agent_deleted</c>, then <c>user_deleted</c>), as the GDPR purge
    /// does; without it nothing is written and <see cref="LicensedUserDeletion.OwnsAgent"/> is set, which is
    /// the admin delete's refusal, race-free.
    /// </summary>
    Task<LicensedUserDeletion> CommitUserDeletedAsync(
        TenantId tenantId, EntityId userId, string? actorUserId, bool deleteOwnedAgent, CancellationToken ct);
}
