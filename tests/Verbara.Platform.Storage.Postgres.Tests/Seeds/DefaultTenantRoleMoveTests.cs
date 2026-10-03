using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Seeds;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// Moving a user's RBAC roles when its <see cref="UserRole"/> changes (<see cref="DefaultTenantRole.MoveAsync"/>
/// over <see cref="PostgresUserRoleStore.MoveAsync"/>), against a real Postgres seeded with the real
/// permission catalog and role templates — the only place the resulting permissions are real: the
/// in-memory role store resolves every user to an empty permission set.
/// </summary>
/// <remarks>
/// Each tenant shape is one the platform produces: roles the boot-time migration cloned, the
/// <c>role_{template}_{tenant}</c> copies a provisioned tenant holds, and the roles setup creates for the
/// first administrators (<c>platform-admin-{host}</c>, <c>admin-{tenant}</c>, granted with no assigner).
/// </remarks>
[Collection("PostgresRbac")]
public sealed class DefaultTenantRoleMoveTests
{
    private static readonly string[] s_administratorPermissions =
        ["system:mfa:manage", "system:audit:view", "system:retention:manage", "security.jwt.rotate", "system:impersonation:manage"];

    private readonly PostgresRbacFixture _fixture;

    public DefaultTenantRoleMoveTests(PostgresRbacFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MoveAsync_ShouldLeaveADemotedAdministratorTheNewRolesPermissionsOnly_WhenTheMigrationClonedTheRoles()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("migrated");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Admin);
        var agent = await NewUserAsync(dataSource, tenant, UserRole.Agent);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        await SetRoleAsync(dataSource, tenant, user, UserRole.Agent);
        var move = await MoveAsync(dataSource, tenant, user, UserRole.Admin, UserRole.Agent);

        move.Removed.Should().Equal("admin");
        move.Granted.Should().Be("agent");
        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal("agent");
        (await PermissionsOfAsync(dataSource, tenant, user)).Should().BeEquivalentTo(
            await PermissionsOfAsync(dataSource, tenant, agent),
            because: "a demoted administrator holds exactly what an agent holds");
    }

    [Fact]
    public async Task MoveAsync_ShouldLeaveADemotedAdministratorTheNewRolesPermissionsOnly_WhenTheTenantWasProvisioned()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("provisioned");
        var roles = new PostgresTenantRoleStore(dataSource);
        foreach (var (templateId, name) in new[] { ("admin", "Admin"), ("agent", "Agent"), ("supervisor", "Supervisor") })
            await roles.CloneFromTemplateAsync(tenant, $"role_{templateId}_provisioned", templateId, name, null, CancellationToken.None);
        var user = await NewUserAsync(dataSource, tenant, UserRole.Admin);
        await new PostgresUserRoleStore(dataSource).AssignAsync(tenant, user, "role_admin_provisioned", "migration", CancellationToken.None);

        var move = await MoveAsync(dataSource, tenant, user, UserRole.Admin, UserRole.Supervisor);

        move.Granted.Should().Be("role_supervisor_provisioned");
        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal("role_supervisor_provisioned");
        (await PermissionsOfAsync(dataSource, tenant, user)).Should().NotContain(s_administratorPermissions);
    }

    [Fact]
    public async Task MoveAsync_ShouldTakeBackTheRolesSetupAndTheMigrationGranted_WhenTheFirstHostAdministratorIsDemoted()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("host");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Admin);
        // Setup: the Platform Admin role, granted with no assigner; then the migration's admin role.
        await new PostgresTenantRoleStore(dataSource).CloneFromTemplateAsync(
            tenant, "platform-admin-host", "platform_admin", "Platform Admin", null, CancellationToken.None);
        await new PostgresUserRoleStore(dataSource).AssignAsync(tenant, user, "platform-admin-host", assignedBy: null, CancellationToken.None);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await PermissionsOfAsync(dataSource, tenant, user)).Should().Contain("billing:credits:grant");

        await SetRoleAsync(dataSource, tenant, user, UserRole.Agent);
        await MoveAsync(dataSource, tenant, user, UserRole.Admin, UserRole.Agent);

        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal("agent");
        var permissions = await PermissionsOfAsync(dataSource, tenant, user);
        permissions.Should().NotContain(s_administratorPermissions);
        permissions.Should().NotContain("billing:credits:grant");
    }

    [Fact]
    public async Task MoveAsync_ShouldTakeBackTheAdminRoleSetupCreated_WhenTheFirstCustomerAdministratorIsDemoted()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("acme");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Admin);
        await new PostgresTenantRoleStore(dataSource).CloneFromTemplateAsync(
            tenant, "admin-acme", "admin", "Admin", null, CancellationToken.None);
        await new PostgresUserRoleStore(dataSource).AssignAsync(tenant, user, "admin-acme", assignedBy: null, CancellationToken.None);

        var move = await MoveAsync(dataSource, tenant, user, UserRole.Admin, UserRole.Agent);

        move.Removed.Should().Equal("admin-acme");
        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal("agent");
        (await PermissionsOfAsync(dataSource, tenant, user)).Should().NotContain(s_administratorPermissions);
    }

    [Fact]
    public async Task MoveAsync_ShouldKeepACustomRoleMadeFromTheAdminTemplate_WhenAnAdministratorIsDemoted()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("custom");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Admin);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        await new PostgresTenantRoleStore(dataSource).CloneFromTemplateAsync(
            tenant, "auditors", "admin", "Auditors", null, CancellationToken.None);
        await new PostgresUserRoleStore(dataSource).AssignAsync(tenant, user, "auditors", "an-administrator", CancellationToken.None);

        await MoveAsync(dataSource, tenant, user, UserRole.Admin, UserRole.Agent);

        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().BeEquivalentTo(["agent", "auditors"]);
    }

    [Fact]
    public async Task MoveAsync_ShouldGrantTheAdministratorRole_WhenAnAgentIsPromoted()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("promoted");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Agent);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        await SetRoleAsync(dataSource, tenant, user, UserRole.Admin);
        await MoveAsync(dataSource, tenant, user, UserRole.Agent, UserRole.Admin);

        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal("admin");
        (await PermissionsOfAsync(dataSource, tenant, user)).Should().Contain(s_administratorPermissions);
    }

    // ─── PostgresUserRoleStore.MoveAsync, the one statement ──────────────────

    [Fact]
    public async Task MoveAsync_ShouldRemoveNothing_WhenTheRoleToAssignDoesNotExist()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("atomic");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Admin);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        var store = new PostgresUserRoleStore(dataSource);

        var move = () => store.MoveAsync(tenant, user, ["admin"], "no-such-role", "default-role", CancellationToken.None);

        (await move.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal("admin");
    }

    [Fact]
    public async Task MoveAsync_ShouldReturnOnlyTheRolesTheUserHeld_WhenAskedToRemoveMore()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("returned");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Admin);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        var removed = await new PostgresUserRoleStore(dataSource).MoveAsync(
            tenant, user, ["admin", "system_admin", "supervisor"], "agent", "default-role", CancellationToken.None);

        removed.Should().Equal("admin");
        var grants = await new PostgresUserRoleStore(dataSource).GetRolesForUserAsync(tenant, user, CancellationToken.None);
        grants.Should().ContainSingle().Which.AssignedBy.Should().Be("default-role");
    }

    [Fact]
    public async Task MoveAsync_ShouldKeepTheAssignerOfTheNewRole_WhenTheUserAlreadyHoldsIt()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("kept");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Admin);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        var store = new PostgresUserRoleStore(dataSource);
        await store.AssignAsync(tenant, user, "agent", "an-administrator", CancellationToken.None);

        var removed = await store.MoveAsync(tenant, user, ["admin", "agent"], "agent", "default-role", CancellationToken.None);

        removed.Should().Equal("admin");
        (await store.GetRolesForUserAsync(tenant, user, CancellationToken.None))
            .Should().ContainSingle().Which.AssignedBy.Should().Be("an-administrator");
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

    private static Task<DefaultRoleMove> MoveAsync(
        NpgsqlDataSource dataSource, TenantId tenant, EntityId user, UserRole previousRole, UserRole role) =>
        DefaultTenantRole.MoveAsync(
            new PostgresTenantRoleStore(dataSource),
            new PostgresRoleTemplateStore(dataSource),
            new PostgresUserRoleStore(dataSource),
            tenant, user, previousRole, role, CancellationToken.None);

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

    private static async Task SetRoleAsync(NpgsqlDataSource dataSource, TenantId tenant, EntityId user, UserRole role)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE users SET role = @Role WHERE tenant_id = @TenantId AND user_id = @UserId");
        cmd.Parameters.Add(new NpgsqlParameter("Role", (int)role));
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenant.Value));
        cmd.Parameters.Add(new NpgsqlParameter("UserId", user.Value));
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task<IReadOnlySet<string>> PermissionsOfAsync(NpgsqlDataSource dataSource, TenantId tenant, EntityId user) =>
        new PostgresUserRoleStore(dataSource).GetEffectivePermissionsAsync(tenant, user, CancellationToken.None);

    private static async Task<List<string>> RoleIdsOfAsync(NpgsqlDataSource dataSource, TenantId tenant, EntityId user) =>
        (await new PostgresUserRoleStore(dataSource).GetRolesForUserAsync(tenant, user, CancellationToken.None))
            .Select(a => a.RoleId)
            .ToList();
}
