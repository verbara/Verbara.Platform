using Npgsql;
using Verbara.Platform.Storage.Postgres.Seeds;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// contacts-writes-follow-their-permissions task 1.7 (design D6; third spec audit F3, fourth spec audit F2): the two roles
/// setup creates, <c>admin-{tenant}</c> (cloned from <c>admin</c>) and <c>platform-admin-{tenant}</c> (cloned from
/// <c>platform_admin</c>), gain <c>contacts:contact:delete</c> at start-up, and only that id; an administrator's other
/// edits stay, a second run adds nothing, and roles with another source or another id are left as they are.
/// </summary>
[Collection("PostgresRbac")]
public sealed class SetupRoleDeleteGrantMigrationTests
{
    private const string Delete = "contacts:contact:delete";
    private const string Tenant = "acme-setup";
    private const string OtherSourceTenant = "acme-other-source";
    private const string BareTenant = "acme-no-setup-roles";
    private const string RemovedGrant = "queues:queue:view";

    private readonly PostgresRbacFixture _fixture;

    public SetupRoleDeleteGrantMigrationTests(PostgresRbacFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MigrateExistingUsers_ShouldGrantDeleteToSetupsTwoRolesOnly_WhenTheyWereClonedBeforeTheChange()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await PermissionSeeder.SeedAsync(dataSource, CancellationToken.None);
        await RoleTemplateSeeder.SeedAsync(dataSource, CancellationToken.None);

        // Tenant as setup and an administrator left it before the change.
        await ExecuteAsync(dataSource, "INSERT INTO users (user_id, tenant_id, role) VALUES ('u-admin', @T, 2), ('u-platform', @T, 2)", Tenant);
        var adminCustom = await CustomGrantForAsync(dataSource, "admin");
        var platformCustom = await CustomGrantForAsync(dataSource, "platform_admin");
        await CloneAsync(dataSource, Tenant, $"admin-{Tenant}", "Admin", "admin", adminCustom);
        await CloneAsync(dataSource, Tenant, $"platform-admin-{Tenant}", "Platform Admin", "platform_admin", platformCustom);
        await ExecuteAsync(dataSource,
            $"INSERT INTO user_roles (tenant_id, user_id, role_id, assigned_at) VALUES (@T, 'u-admin', 'admin-{Tenant}', now()), " +
            $"(@T, 'u-platform', 'platform-admin-{Tenant}', now())", Tenant);

        // A tenant whose roles carry setup's ids but another source, plus a platform_admin-id role.
        await ExecuteAsync(dataSource, "INSERT INTO users (user_id, tenant_id, role) VALUES ('u-other', @T, 0)", OtherSourceTenant);
        await CloneAsync(dataSource, OtherSourceTenant, $"admin-{OtherSourceTenant}", "Front desk", "agent", custom: null);
        await CloneAsync(dataSource, OtherSourceTenant, $"platform-admin-{OtherSourceTenant}", "Ops", "supervisor", custom: null);
        await CloneAsync(dataSource, OtherSourceTenant, "platform_admin", "Platform Admin", "platform_admin", custom: null);

        // A tenant with no setup role at all.
        await ExecuteAsync(dataSource, "INSERT INTO users (user_id, tenant_id, role) VALUES ('u-bare', @T, 0)", BareTenant);

        (await GrantsAsync(dataSource, Tenant, $"admin-{Tenant}")).Should().NotContain(Delete);
        (await GrantsAsync(dataSource, Tenant, $"platform-admin-{Tenant}")).Should().NotContain(Delete);

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        foreach (var (roleId, custom) in new[] { ($"admin-{Tenant}", adminCustom), ($"platform-admin-{Tenant}", platformCustom) })
        {
            var grants = await GrantsAsync(dataSource, Tenant, roleId);
            grants.Should().Contain(Delete, $"{roleId} is a role setup creates");
            grants.Should().Contain(custom, "an administrator's own grant stays");
            grants.Should().NotContain(RemovedGrant, "a template grant an administrator removed is not restored");
        }

        (await GrantsAsync(dataSource, OtherSourceTenant, $"admin-{OtherSourceTenant}")).Should().NotContain(Delete);
        (await GrantsAsync(dataSource, OtherSourceTenant, $"platform-admin-{OtherSourceTenant}")).Should().NotContain(Delete);
        (await GrantsAsync(dataSource, OtherSourceTenant, "platform_admin")).Should().NotContain(Delete,
            "a platform_admin-id role is re-aligned only by tools/RbacReseed");

        var afterFirst = await CountAsync(dataSource);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await CountAsync(dataSource)).Should().Be(afterFirst, "a second run adds nothing");
    }

    // An id the template does not grant, which an administrator added to its copy. platform_admin grants the whole
    // catalog, so the test catalogues one id of its own for that case.
    private static async Task<string> CustomGrantForAsync(NpgsqlDataSource dataSource, string templateId)
    {
        await using (var insert = dataSource.CreateCommand(
            "INSERT INTO permissions (permission_id, category, resource, action, description, implies) " +
            "VALUES ('custom:grant:test', 'custom', 'grant', 'test', 'A tenant-added grant', '{}') ON CONFLICT DO NOTHING"))
        {
            await insert.ExecuteNonQueryAsync();
        }

        await using var cmd = dataSource.CreateCommand(
            "SELECT permission_id FROM permissions p WHERE NOT EXISTS (SELECT 1 FROM role_template_permissions rtp " +
            "WHERE rtp.template_id = @Template AND rtp.permission_id = p.permission_id) ORDER BY permission_id LIMIT 1");
        cmd.Parameters.Add(new NpgsqlParameter("Template", templateId));
        return await cmd.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException($"the catalog holds no id the {templateId} template does not grant");
    }

    // A tenant role cloned from the template as a release before the change did: every template grant but delete, one
    // template grant removed by an administrator, and one custom grant added.
    private static async Task CloneAsync(
        NpgsqlDataSource dataSource, string tenant, string roleId, string name, string sourceTemplate, string? custom)
    {
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO tenant_roles (tenant_id, role_id, name, description, source_template_id, is_default, created_at) " +
            "VALUES (@T, @R, @N, '', @S, false, now()); " +
            "INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id) " +
            "SELECT @T, @R, permission_id FROM role_template_permissions WHERE template_id = @S " +
            "  AND permission_id NOT IN (@Delete, @Removed); " +
            "INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id) " +
            "SELECT @T, @R, @Custom WHERE @Custom IS NOT NULL");
        cmd.Parameters.Add(new NpgsqlParameter("T", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("R", roleId));
        cmd.Parameters.Add(new NpgsqlParameter("N", name));
        cmd.Parameters.Add(new NpgsqlParameter("S", sourceTemplate));
        cmd.Parameters.Add(new NpgsqlParameter("Delete", Delete));
        cmd.Parameters.Add(new NpgsqlParameter("Removed", RemovedGrant));
        cmd.Parameters.Add(new NpgsqlParameter("Custom", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)custom ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> GrantsAsync(NpgsqlDataSource dataSource, string tenant, string roleId)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT permission_id FROM tenant_role_permissions WHERE tenant_id = @T AND role_id = @R ORDER BY permission_id");
        cmd.Parameters.Add(new NpgsqlParameter("T", tenant));
        cmd.Parameters.Add(new NpgsqlParameter("R", roleId));
        var grants = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            grants.Add(reader.GetString(0));
        return grants;
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT (SELECT count(*) FROM tenant_role_permissions) + (SELECT count(*) FROM tenant_roles) " +
            "+ (SELECT count(*) FROM user_roles)");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, string tenant)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter("T", tenant));
        await cmd.ExecuteNonQueryAsync();
    }
}
