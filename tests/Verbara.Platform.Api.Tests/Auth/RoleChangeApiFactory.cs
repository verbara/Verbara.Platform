using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// The account-status host with a role store whose permissions follow the user's RBAC role
/// assignments, the way <c>PostgresUserRoleStore</c> resolves them (the union of the permissions of
/// the tenant roles the user holds). The in-memory store resolves every user to an empty set, so on it
/// a stale grant and no grant look the same and a role change cannot be seen at a permission gate.
/// </summary>
public sealed class RoleChangeApiFactory : AccountStatusApiFactory
{
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(IUserRoleStore)).ToList())
            services.Remove(d);
        services.AddSingleton<AssignmentBackedUserRoleStore>(sp =>
            new AssignmentBackedUserRoleStore(sp.GetRequiredService<ITenantRoleStore>()));
        services.AddSingleton<IUserRoleStore>(sp => sp.GetRequiredService<AssignmentBackedUserRoleStore>());
    }

    /// <summary>
    /// Saves a tenant role with exactly <paramref name="permissions"/>, replacing one with the same id.
    /// </summary>
    public async Task SaveTenantRoleAsync(
        string tenantId, string roleId, string name, string? sourceTemplateId, params string[] permissions)
    {
        using var scope = Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<ITenantRoleStore>();
        var tenant = new TenantId(tenantId);
        await roles.SaveAsync(
            new TenantRole
            {
                RoleId = roleId,
                TenantId = tenant,
                Name = name,
                SourceTemplateId = sourceTemplateId,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            CancellationToken.None);
        await roles.SetPermissionsAsync(tenant, roleId, permissions, CancellationToken.None);
    }

    public Task AssignAsync(User user, string roleId, string? assignedBy) =>
        Services.GetRequiredService<IUserRoleStore>()
            .AssignAsync(user.TenantId, user.UserId, roleId, assignedBy, CancellationToken.None);

    public async Task<IReadOnlyList<string>> RoleIdsOfAsync(User user) =>
        (await Services.GetRequiredService<IUserRoleStore>()
            .GetRolesForUserAsync(user.TenantId, user.UserId, CancellationToken.None))
        .Select(a => a.RoleId)
        .ToList();

    public Task<IReadOnlyList<UserRoleAssignment>> GrantsOfAsync(User user) =>
        Services.GetRequiredService<IUserRoleStore>()
            .GetRolesForUserAsync(user.TenantId, user.UserId, CancellationToken.None);
}

/// <summary>
/// The real in-memory role assignments, with effective permissions resolved from them: the union of
/// the permissions <see cref="ITenantRoleStore"/> holds for each tenant role the user is assigned.
/// </summary>
public sealed class AssignmentBackedUserRoleStore : IUserRoleStore
{
    private readonly InMemoryUserRoleStore _assignments = new();
    private readonly ITenantRoleStore _tenantRoles;

    public AssignmentBackedUserRoleStore(ITenantRoleStore tenantRoles) => _tenantRoles = tenantRoles;

    public Task<IReadOnlyList<UserRoleAssignment>> GetRolesForUserAsync(TenantId tenantId, EntityId userId, CancellationToken ct) =>
        _assignments.GetRolesForUserAsync(tenantId, userId, ct);

    public Task AssignAsync(TenantId tenantId, EntityId userId, string roleId, string? assignedBy, CancellationToken ct) =>
        _assignments.AssignAsync(tenantId, userId, roleId, assignedBy, ct);

    public Task RemoveAsync(TenantId tenantId, EntityId userId, string roleId, CancellationToken ct) =>
        _assignments.RemoveAsync(tenantId, userId, roleId, ct);

    public Task ReplaceAllAsync(TenantId tenantId, EntityId userId, IReadOnlyList<string> roleIds, string? assignedBy, CancellationToken ct) =>
        _assignments.ReplaceAllAsync(tenantId, userId, roleIds, assignedBy, ct);

    public Task<IReadOnlyList<string>> MoveAsync(
        TenantId tenantId, EntityId userId, IReadOnlyCollection<string> fromRoleIds, string? toRoleId, string? assignedBy,
        CancellationToken ct) =>
        _assignments.MoveAsync(tenantId, userId, fromRoleIds, toRoleId, assignedBy, ct);

    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(TenantId tenantId, EntityId userId, CancellationToken ct)
    {
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in await _assignments.GetRolesForUserAsync(tenantId, userId, ct))
            permissions.UnionWith(await _tenantRoles.GetPermissionsAsync(tenantId, assignment.RoleId, ct));
        return permissions;
    }
}
