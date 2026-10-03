using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Seeds;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// The role migration that runs at every start (<see cref="RbacMigrationSeeder.MigrateExistingUsersAsync"/>)
/// keeps the RBAC role it grants for a user's <see cref="UserRole"/> in step with that role: a grant it
/// made (or a user-creation path made) for a role the user no longer has is removed, so a user demoted
/// before this release stops holding the old role's permissions at the next start. Against a real
/// Postgres seeded with the real permission catalog and role templates: the in-memory role store
/// resolves every user to an empty permission set and cannot tell a stale grant from none.
/// </summary>
[Collection("PostgresRbac")]
public sealed class RbacMigrationReconcileTests
{
    private const string AdminOnlyPermission = "system:mfa:manage";
    private const string AgentPermission = "contacts:conversation:transfer";

    private readonly PostgresRbacFixture _fixture;

    public RbacMigrationReconcileTests(PostgresRbacFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldRemoveTheFormerRolesGrant_WhenTheUserWasDemotedSinceTheLastRun()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("acme");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Admin);
        await MigrateAsync(dataSource);

        await SetRoleAsync(dataSource, tenant, user, UserRole.Agent);
        await MigrateAsync(dataSource);

        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal("agent");
        var permissions = await PermissionsOfAsync(dataSource, tenant, user);
        permissions.Should().Contain(AgentPermission);
        permissions.Should().NotContain(AdminOnlyPermission);
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldRemoveTheFormerRolesGrant_WhenTheTenantsRolesWereProvisionedUnderTheirOwnIds()
    {
        // A provisioned tenant holds role_{template}_{tenant} copies named like the templates, so the
        // migration resolves the Admin and Agent roles by name.
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("provisioned");
        var roles = new PostgresTenantRoleStore(dataSource);
        await roles.CloneFromTemplateAsync(tenant, "role_admin_provisioned", "admin", "Admin", null, CancellationToken.None);
        await roles.CloneFromTemplateAsync(tenant, "role_agent_provisioned", "agent", "Agent", null, CancellationToken.None);
        var user = await NewUserAsync(dataSource, tenant, UserRole.Admin);
        await MigrateAsync(dataSource);
        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal("role_admin_provisioned");

        await SetRoleAsync(dataSource, tenant, user, UserRole.Agent);
        await MigrateAsync(dataSource);

        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal("role_agent_provisioned");
        (await PermissionsOfAsync(dataSource, tenant, user)).Should().NotContain(AdminOnlyPermission);
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldRemoveAGrantMadeAtCreation_WhenTheUsersRoleChangedAfterIt()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("created");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Agent);
        await DefaultTenantRole.GrantAsync(
            new PostgresTenantRoleStore(dataSource), new PostgresRoleTemplateStore(dataSource), new PostgresUserRoleStore(dataSource),
            tenant, user, UserRole.Agent, CancellationToken.None);

        await SetRoleAsync(dataSource, tenant, user, UserRole.Supervisor);
        await MigrateAsync(dataSource);

        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal("supervisor");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldKeepGrantsItDidNotMake_WhenTheyAreNotTheRoleOfTheUsersRole()
    {
        // An administrator's explicit grant, and the Admin role setup grants (recorded with no assigner),
        // are not the migration's to take back: neither says it followed from the user's role.
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("explicit");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Agent);
        await MigrateAsync(dataSource);
        var userRoles = new PostgresUserRoleStore(dataSource);
        await userRoles.AssignAsync(tenant, user, "supervisor", "an-administrator", CancellationToken.None);
        await userRoles.AssignAsync(tenant, user, "admin", assignedBy: null, CancellationToken.None);

        await MigrateAsync(dataSource);

        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().BeEquivalentTo(["agent", "supervisor", "admin"]);
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldKeepTheGrant_WhenTheUsersRoleIsUnchanged()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("steady");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Supervisor);

        await MigrateAsync(dataSource);
        await MigrateAsync(dataSource);

        var grants = await new PostgresUserRoleStore(dataSource).GetRolesForUserAsync(tenant, user, CancellationToken.None);
        grants.Should().ContainSingle().Which.RoleId.Should().Be("supervisor");
        grants[0].AssignedBy.Should().Be("migration");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private async Task<NpgsqlDataSource> SeededAsync()
    {
        await _fixture.ResetAsync();
        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await PermissionSeeder.SeedAsync(dataSource, CancellationToken.None);
        await RoleTemplateSeeder.SeedAsync(dataSource, CancellationToken.None);
        return dataSource;
    }

    private static Task MigrateAsync(NpgsqlDataSource dataSource) =>
        RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

    private static async Task<EntityId> NewUserAsync(NpgsqlDataSource dataSource, TenantId tenant, UserRole role)
    {
        var userId = EntityId.From($"user-{Guid.NewGuid():N}");
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO users (user_id, tenant_id, role) VALUES (@UserId, @TenantId, @Role)");
        cmd.Parameters.Add(new NpgsqlParameter("UserId", userId.Value));
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenant.Value));
        cmd.Parameters.Add(new NpgsqlParameter("Role", (int)role));
        await cmd.ExecuteNonQueryAsync();
        return userId;
    }

    /// <summary>Changes users.role and nothing else, as every role change before this release did.</summary>
    private static async Task SetRoleAsync(NpgsqlDataSource dataSource, TenantId tenant, EntityId user, UserRole role)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE users SET role = @Role WHERE tenant_id = @TenantId AND user_id = @UserId");
        cmd.Parameters.Add(new NpgsqlParameter("Role", (int)role));
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenant.Value));
        cmd.Parameters.Add(new NpgsqlParameter("UserId", user.Value));
        (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private static Task<IReadOnlySet<string>> PermissionsOfAsync(NpgsqlDataSource dataSource, TenantId tenant, EntityId user) =>
        new PostgresUserRoleStore(dataSource).GetEffectivePermissionsAsync(tenant, user, CancellationToken.None);

    private static async Task<List<string>> RoleIdsOfAsync(NpgsqlDataSource dataSource, TenantId tenant, EntityId user) =>
        (await new PostgresUserRoleStore(dataSource).GetRolesForUserAsync(tenant, user, CancellationToken.None))
            .Select(a => a.RoleId)
            .ToList();
}
