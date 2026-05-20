using Verbara.Platform.Storage.Postgres.Seeds;
using Npgsql;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// Coverage for <see cref="RoleTemplateSeeder.ReseedExistingTenantsAsync"/>.
/// Validates the R5.2 PC.3 (B.7) re-seed migration: existing tenants whose
/// <c>platform_admin</c> role rows pre-date a permission catalog expansion
/// must pick up the new permissions and shed any retired ones.
/// </summary>
[Collection("PostgresRbac")]
public sealed class RoleTemplateSeederReseedTests
{
    private readonly PostgresRbacFixture _fixture;

    public RoleTemplateSeederReseedTests(PostgresRbacFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ReseedExistingTenantsAsync_ShouldAddNewPermissions_WhenAllPermissionsExpanded()
    {
        await _fixture.ResetAsync();
        await using var conn = await OpenAsync();

        // Seed only a subset of permissions on tenant_a, simulating a tenant
        // provisioned before the R5.2 P0.9 catalog expansion.
        var legacyPerms = new[]
        {
            "users:user:view",
            "users:user:create",
        };
        await CreateAllPermissionsAsync(conn);
        await CreateTenantWithPermissionsAsync(conn, "tenant-a", legacyPerms);

        var report = await RoleTemplateSeeder.ReseedExistingTenantsAsync(
            ["tenant-a"], conn, CancellationToken.None);

        report.Should().ContainKey("tenant-a");
        var canonical = RoleTemplateSeeder.GetCanonicalPermissions();
        report["tenant-a"].PermissionsAdded.Should().Be(canonical.Count - legacyPerms.Length);
        report["tenant-a"].PermissionsRemoved.Should().Be(0);

        var stored = await ReadTenantPermissionsAsync(conn, "tenant-a");
        stored.Should().Contain("security.mfa.admin");
        stored.Should().Contain("audit.read");
        stored.Should().Contain("audit.export");
        stored.Should().Contain("retention.read");
        stored.Should().Contain("retention.manage");
        stored.Should().Contain("tenant.settings.write");
        stored.Should().Contain("security.impersonation.manage");
        stored.Should().BeEquivalentTo(canonical);
    }

    [Fact]
    public async Task ReseedExistingTenantsAsync_ShouldRemoveDeprecatedPermissions_WhenAllPermissionsContracts()
    {
        await _fixture.ResetAsync();
        await using var conn = await OpenAsync();

        await CreateAllPermissionsAsync(conn);
        // Insert a permission that is in the catalog plus a fake one that
        // simulates a permission that was retired in the new code rollout.
        await InsertExtraPermissionAsync(conn, "legacy:retired:permission");

        // Tenant currently has the canonical set + the retired permission.
        var canonical = RoleTemplateSeeder.GetCanonicalPermissions().ToList();
        var seededPerms = canonical.Concat(["legacy:retired:permission"]).ToArray();
        await CreateTenantWithPermissionsAsync(conn, "tenant-b", seededPerms);

        var report = await RoleTemplateSeeder.ReseedExistingTenantsAsync(
            ["tenant-b"], conn, CancellationToken.None);

        report["tenant-b"].PermissionsAdded.Should().Be(0);
        report["tenant-b"].PermissionsRemoved.Should().Be(1);

        var stored = await ReadTenantPermissionsAsync(conn, "tenant-b");
        stored.Should().NotContain("legacy:retired:permission");
        stored.Should().BeEquivalentTo(canonical);
    }

    [Fact]
    public async Task ReseedExistingTenantsAsync_ShouldBeIdempotent_WhenRunTwice()
    {
        await _fixture.ResetAsync();
        await using var conn = await OpenAsync();

        await CreateAllPermissionsAsync(conn);
        await CreateTenantWithPermissionsAsync(conn, "tenant-c",
            ["users:user:view"]);

        // First run drives state to the canonical catalog.
        var first = await RoleTemplateSeeder.ReseedExistingTenantsAsync(
            ["tenant-c"], conn, CancellationToken.None);
        first["tenant-c"].PermissionsAdded.Should().BeGreaterThan(0);

        // Second run on the same tenant must be a no-op.
        var second = await RoleTemplateSeeder.ReseedExistingTenantsAsync(
            ["tenant-c"], conn, CancellationToken.None);
        second["tenant-c"].PermissionsAdded.Should().Be(0);
        second["tenant-c"].PermissionsRemoved.Should().Be(0);
    }

    [Fact]
    public async Task ReseedExistingTenantsAsync_ShouldHandleEmptyTenantList_Gracefully()
    {
        await _fixture.ResetAsync();
        await using var conn = await OpenAsync();
        await CreateAllPermissionsAsync(conn);

        var report = await RoleTemplateSeeder.ReseedExistingTenantsAsync(
            Array.Empty<string>(), conn, CancellationToken.None);

        report.Should().BeEmpty();
    }

    [Fact]
    public async Task ReseedExistingTenantsAsync_ShouldOnlyAffectPlatformAdminRole_NotOtherRoles()
    {
        await _fixture.ResetAsync();
        await using var conn = await OpenAsync();

        await CreateAllPermissionsAsync(conn);
        await CreateTenantWithPermissionsAsync(conn, "tenant-d",
            ["users:user:view"]);

        // Add an `agent` role to the same tenant with a tiny permission set
        // unrelated to the canonical platform_admin catalog.
        await CreateRoleAsync(conn, "tenant-d", "agent",
            ["contacts:conversation:handle"]);

        await RoleTemplateSeeder.ReseedExistingTenantsAsync(
            ["tenant-d"], conn, CancellationToken.None);

        // platform_admin should now hold the canonical catalog.
        var adminPerms = await ReadTenantPermissionsAsync(conn, "tenant-d");
        adminPerms.Should().BeEquivalentTo(RoleTemplateSeeder.GetCanonicalPermissions());

        // agent role must remain untouched — exactly the one permission seeded.
        var agentPerms = await ReadRolePermissionsAsync(conn, "tenant-d", "agent");
        agentPerms.Should().BeEquivalentTo(["contacts:conversation:handle"]);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task CreateAllPermissionsAsync(NpgsqlConnection conn)
    {
        // Insert every permission referenced by RoleTemplateSeeder.AllPermissions
        // so foreign keys on tenant_role_permissions and role_template_permissions
        // can be satisfied. Real production seeding uses PermissionSeeder; we
        // only need the rows to exist, not the rich metadata.
        const string sql =
            "INSERT INTO permissions (permission_id, category, resource, action, description, implies) " +
            "VALUES (@Id, 'test', 'test', 'test', 'test fixture row', ARRAY[]::text[]) " +
            "ON CONFLICT (permission_id) DO NOTHING";

        foreach (var perm in RoleTemplateSeeder.GetCanonicalPermissions())
        {
            await conn.ExecuteAsync(sql,
                p => p.Add(new NpgsqlParameter("Id", perm)), null, CancellationToken.None);
        }
    }

    private static async Task InsertExtraPermissionAsync(NpgsqlConnection conn, string permissionId)
    {
        await conn.ExecuteAsync(
            "INSERT INTO permissions (permission_id, category, resource, action, description, implies) " +
            "VALUES (@Id, 'legacy', 'legacy', 'legacy', 'retired in current code', ARRAY[]::text[])",
            p => p.Add(new NpgsqlParameter("Id", permissionId)), null, CancellationToken.None);
    }

    private static async Task CreateTenantWithPermissionsAsync(
        NpgsqlConnection conn, string tenantId, IEnumerable<string> permissions)
    {
        await CreateRoleAsync(conn, tenantId, RoleTemplateSeeder.PlatformAdminRoleId, permissions);
    }

    private static async Task CreateRoleAsync(
        NpgsqlConnection conn, string tenantId, string roleId, IEnumerable<string> permissions)
    {
        await conn.ExecuteAsync(
            "INSERT INTO tenant_roles (role_id, tenant_id, name, description, source_template_id, is_default) " +
            "VALUES (@RoleId, @TenantId, @Name, 'fixture role', @RoleId, true) " +
            "ON CONFLICT (tenant_id, role_id) DO NOTHING",
            p =>
            {
                p.Add(new NpgsqlParameter("RoleId", roleId));
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("Name", roleId));
            }, null, CancellationToken.None);

        foreach (var perm in permissions)
        {
            await conn.ExecuteAsync(
                "INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id) " +
                "VALUES (@TenantId, @RoleId, @PermissionId) " +
                "ON CONFLICT (tenant_id, role_id, permission_id) DO NOTHING",
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId));
                    p.Add(new NpgsqlParameter("RoleId", roleId));
                    p.Add(new NpgsqlParameter("PermissionId", perm));
                }, null, CancellationToken.None);
        }
    }

    private static Task<IReadOnlyCollection<string>> ReadTenantPermissionsAsync(
        NpgsqlConnection conn, string tenantId) =>
        ReadRolePermissionsAsync(conn, tenantId, RoleTemplateSeeder.PlatformAdminRoleId);

    private static async Task<IReadOnlyCollection<string>> ReadRolePermissionsAsync(
        NpgsqlConnection conn, string tenantId, string roleId)
    {
        var rows = new List<string>();
        await using var cmd = new NpgsqlCommand(
            "SELECT permission_id FROM tenant_role_permissions " +
            "WHERE tenant_id = @TenantId AND role_id = @RoleId", conn);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("RoleId", roleId));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }
}
