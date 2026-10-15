using Microsoft.Extensions.Logging;

namespace Verbara.Platform.Storage.Postgres.Seeds;

/// <summary>
/// The events of the boot-time role migration (<see cref="RbacMigrationSeeder"/>). The host logs the
/// aggregate outcome (<c>RbacSeedLog</c>, Platform/ADR-0037); these are the per-tenant events behind it.
/// </summary>
internal static partial class RbacMigrationLog
{
    /// <summary>The logger category the host creates for the migration.</summary>
    public const string Category = "Verbara.Platform.Storage.Postgres.Seeds.RbacMigrationSeeder";

    [LoggerMessage(EventId = 3700, Level = LogLevel.Information,
        Message = "RBAC migration: tenant {TenantId} already owns role {ExistingRoleId} named like template " +
                  "{TemplateId}; the template was not cloned, so that role keeps its own grants.")]
    public static partial void CloneSkipped(ILogger logger, string tenantId, string templateId, string existingRoleId);

    [LoggerMessage(EventId = 3701, Level = LogLevel.Error,
        Message = "RBAC migration: tenant {TenantId} failed and was rolled back; the other tenants continue. " +
                  "It is retried at the next start.")]
    public static partial void TenantFailed(ILogger logger, string tenantId, Exception exception);

    [LoggerMessage(EventId = 3702, Level = LogLevel.Error,
        Message = "RBAC migration: {FailedCount} of {TenantCount} tenants failed and were rolled back: {FailedTenants}.")]
    public static partial void MigrationIncomplete(ILogger logger, int failedCount, int tenantCount, string failedTenants);

    [LoggerMessage(EventId = 3703, Level = LogLevel.Warning,
        Message = "RBAC migration: tenant {TenantId} user {UserId} was attached to role {RoleId} while holding " +
                  "{HeldRoleIds}, granted by the migration or at user creation on a role that no template's id, name or " +
                  "source template matches any more; such a role was recognised by its former name only.")]
    public static partial void NameDependentAttach(ILogger logger, string tenantId, string userId, string roleId, string heldRoleIds);
}
