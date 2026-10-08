using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The admin user writes that can change who counts as a licensed agent, routed through the licensed-agent
/// writer (licensed-agent-metering, design D5): a status change and a deletion commit with their ledger row.
/// The write happens outside the user-store decorators, so the auth cache is dropped here exactly as the
/// cache decorator drops it for its own writes (this replica and, through Redis, the others).
/// </summary>
internal static class LicensedUserWrites
{
    public static Task<AdminFieldsWriteResult> UpdateAdminFieldsAsync(
        IUserStore users, ILicensedUserChangeWriter writer, TenantId tenantId, EntityId userId, AdminFieldsChange change,
        DateTimeOffset updatedAt, string? updatedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Task<AdminFieldsWriteResult> Write() =>
            writer.CommitUserStatusChangedAsync(tenantId, userId, change, updatedAt, updatedBy, ct);
        return users is CachedUserStore cached ? cached.WriteThroughAsync(tenantId, userId, Write, ct) : Write();
    }

    public static Task<LicensedUserDeletion> DeleteAsync(
        IUserStore users, ILicensedUserChangeWriter writer, TenantId tenantId, EntityId userId, string? actorUserId,
        bool deleteOwnedAgent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Task<LicensedUserDeletion> Write() =>
            writer.CommitUserDeletedAsync(tenantId, userId, actorUserId, deleteOwnedAgent, ct);
        return users is CachedUserStore cached ? cached.WriteThroughAsync(tenantId, userId, Write, ct) : Write();
    }
}
