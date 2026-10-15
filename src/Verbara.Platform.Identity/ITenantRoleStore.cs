using Verbara.Platform.Core;

namespace Verbara.Platform.Identity;

public interface ITenantRoleStore
{
    Task<IReadOnlyList<TenantRole>> ListAsync(TenantId tenantId, CancellationToken ct);
    Task<TenantRole?> GetByIdAsync(TenantId tenantId, string roleId, CancellationToken ct);

    /// <summary>
    /// Creates <paramref name="role"/>, or updates its name, description and default flag. An update never
    /// changes <see cref="TenantRole.SourceTemplateId"/>: the template a role was cloned from survives a
    /// rename.
    /// </summary>
    Task SaveAsync(TenantRole role, CancellationToken ct);

    Task DeleteAsync(TenantId tenantId, string roleId, CancellationToken ct);
    Task<IReadOnlyList<string>> GetPermissionsAsync(TenantId tenantId, string roleId, CancellationToken ct);
    Task SetPermissionsAsync(TenantId tenantId, string roleId, IReadOnlyList<string> permissionIds, CancellationToken ct);
    Task CloneFromTemplateAsync(TenantId tenantId, string roleId, string templateId, string name, string? description, CancellationToken ct);
    Task<int> GetUserCountAsync(TenantId tenantId, string roleId, CancellationToken ct);
}
