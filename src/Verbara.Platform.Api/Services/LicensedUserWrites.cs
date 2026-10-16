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

    // add-user-roles-user-foreign-key (design D6): deleting the user removes its user_roles rows (ON DELETE
    // CASCADE), so the permissions resolved for it are dropped too — on this replica, and through Redis on the
    // others — also when the delete was refused, as the user cache is. Without this, this replica kept serving
    // the deleted user's cached permissions until the entry expired.
    public static async Task<LicensedUserDeletion> DeleteAsync(
        IUserStore users, ILicensedUserChangeWriter writer, PermissionResolver? permissions, TenantId tenantId,
        EntityId userId, string? actorUserId, bool deleteOwnedAgent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Task<LicensedUserDeletion> Write() =>
            writer.CommitUserDeletedAsync(tenantId, userId, actorUserId, deleteOwnedAgent, ct);
        var deletion = users is CachedUserStore cached
            ? await cached.WriteThroughAsync(tenantId, userId, Write, ct).ConfigureAwait(false)
            : await Write().ConfigureAwait(false);
        permissions?.InvalidateUser(tenantId, userId);
        return deletion;
    }
}
