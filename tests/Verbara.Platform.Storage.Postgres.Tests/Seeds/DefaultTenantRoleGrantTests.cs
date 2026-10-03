using Npgsql;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Storage.Postgres.Seeds;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// The default role a new user is granted (<see cref="DefaultTenantRole.GrantAsync"/>), against a real
/// Postgres seeded with the real permission catalog and role templates. Its permissions are only real
/// here: the in-memory role store resolves every user to an empty permission set, so a test on it
/// cannot tell a granted role from no role at all.
/// </summary>
/// <remarks>
/// Each tenant shape is one the platform produces: a tenant the boot-time migration has never seen
/// (created since the last start), one provisioned with <c>role_{template}_{tenant}</c> copies, one
/// whose Admin role setup created as <c>admin-{tenant}</c>. After the grant, that migration — which
/// runs at every start — must settle on the same single role, not add a second one.
/// </remarks>
[Collection("PostgresRbac")]
public sealed class DefaultTenantRoleGrantTests
{
    private const string TransferPermission = "contacts:conversation:transfer";

    private readonly PostgresRbacFixture _fixture;

    public DefaultTenantRoleGrantTests(PostgresRbacFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GrantAsync_ShouldCloneTheTemplateAndGrantItsPermissions_WhenTheTenantHasNoRolesYet()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("fresh");
        var user = await NewUserAsync(dataSource, tenant, UserRole.Agent);

        var granted = await GrantAsync(dataSource, tenant, user, UserRole.Agent);

        granted.Should().Be("agent");
        (await PermissionsOfAsync(dataSource, tenant, user)).Should().Contain(TransferPermission);
        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal("agent");
    }

    [Fact]
    public async Task GrantAsync_ShouldGrantTheProvisionedCopy_WhenTheTenantWasProvisionedWithItsOwnRoleIds()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("provisioned");
        var roles = new PostgresTenantRoleStore(dataSource);
        await roles.CloneFromTemplateAsync(tenant, "role_agent_provisioned", "agent", "Agent", "Frontline agent", CancellationToken.None);
        var user = await NewUserAsync(dataSource, tenant, UserRole.Agent);

        var granted = await GrantAsync(dataSource, tenant, user, UserRole.Agent);

        granted.Should().Be("role_agent_provisioned");
        (await PermissionsOfAsync(dataSource, tenant, user)).Should().Contain(TransferPermission);
        (await roles.ListAsync(tenant, CancellationToken.None)).Select(r => r.RoleId)
            .Should().Equal("role_agent_provisioned");
    }

    [Fact]
    public async Task GrantAsync_ShouldGrantTheAdminRoleSetupCreated_WhenTheUserIsAnAdmin()
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("setup");
        await new PostgresTenantRoleStore(dataSource).CloneFromTemplateAsync(
            tenant, "admin-setup", "admin", "Admin", "Full administrative access", CancellationToken.None);
        var user = await NewUserAsync(dataSource, tenant, UserRole.Admin);

        var granted = await GrantAsync(dataSource, tenant, user, UserRole.Admin);

        granted.Should().Be("admin-setup");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("role_agent_converge")]
    public async Task GrantAsync_ShouldLeaveTheBootMigrationNothingToAdd_WhenItRunsAfterTheGrant(string? provisionedRoleId)
    {
        await using var dataSource = await SeededAsync();
        var tenant = new TenantId("converge");
        if (provisionedRoleId is not null)
        {
            await new PostgresTenantRoleStore(dataSource).CloneFromTemplateAsync(
                tenant, provisionedRoleId, "agent", "Agent", "Frontline agent", CancellationToken.None);
        }
        var user = await NewUserAsync(dataSource, tenant, UserRole.Agent);
        var granted = await GrantAsync(dataSource, tenant, user, UserRole.Agent);

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        granted.Should().NotBeNull();
        (await RoleIdsOfAsync(dataSource, tenant, user)).Should().Equal(granted!);
    }

    private async Task<NpgsqlDataSource> SeededAsync()
    {
        await _fixture.ResetAsync();
        var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await PermissionSeeder.SeedAsync(dataSource, CancellationToken.None);
        await RoleTemplateSeeder.SeedAsync(dataSource, CancellationToken.None);
        return dataSource;
    }

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

    private static Task<string?> GrantAsync(NpgsqlDataSource dataSource, TenantId tenant, EntityId user, UserRole role) =>
        DefaultTenantRole.GrantAsync(
            new PostgresTenantRoleStore(dataSource),
            new PostgresRoleTemplateStore(dataSource),
            new PostgresUserRoleStore(dataSource),
            tenant, user, role, CancellationToken.None);

    private static Task<IReadOnlySet<string>> PermissionsOfAsync(NpgsqlDataSource dataSource, TenantId tenant, EntityId user) =>
        new PostgresUserRoleStore(dataSource).GetEffectivePermissionsAsync(tenant, user, CancellationToken.None);

    private static async Task<List<string>> RoleIdsOfAsync(NpgsqlDataSource dataSource, TenantId tenant, EntityId user) =>
        (await new PostgresUserRoleStore(dataSource).GetRolesForUserAsync(tenant, user, CancellationToken.None))
            .Select(a => a.RoleId)
            .ToList();
}
