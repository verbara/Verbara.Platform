using Verbara.Platform.Core;

namespace Verbara.Platform.Identity;

public interface IUserRoleStore
{
    Task<IReadOnlyList<UserRoleAssignment>> GetRolesForUserAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
    Task AssignAsync(TenantId tenantId, EntityId userId, string roleId, string? assignedBy, CancellationToken ct);
    Task RemoveAsync(TenantId tenantId, EntityId userId, string roleId, CancellationToken ct);
    Task ReplaceAllAsync(TenantId tenantId, EntityId userId, IReadOnlyList<string> roleIds, string? assignedBy, CancellationToken ct);

    /// <summary>
    /// In one write, removes the user's assignments to <paramref name="fromRoleIds"/> (all but
    /// <paramref name="toRoleId"/>) and assigns <paramref name="toRoleId"/>, which is left as it is when
    /// the user already holds it; a <see langword="null"/> <paramref name="toRoleId"/> only removes.
    /// Either both happen or neither does.
    /// </summary>
    /// <returns>The roles among <paramref name="fromRoleIds"/> the user held and no longer holds.</returns>
    Task<IReadOnlyList<string>> MoveAsync(
        TenantId tenantId, EntityId userId, IReadOnlyCollection<string> fromRoleIds, string? toRoleId, string? assignedBy,
        CancellationToken ct);

    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(TenantId tenantId, EntityId userId, CancellationToken ct);
}
