using Npgsql;
using Verbara.Platform.Storage.Postgres.Seeds;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// contacts-writes-follow-their-permissions task 1.3 (design D4, D5): <c>contacts:contact:delete</c> is catalogued
/// (implying <c>contacts:contact:view</c> as catalog metadata only) and granted to the admin template family only; the
/// <c>api</c> template gains <c>contacts:contact:create</c> and <c>contacts:contact:edit</c>, never delete.
/// </summary>
public sealed class ContactWritePermissionSeedTests
{
    public const string Delete = "contacts:contact:delete";
    public const string Create = "contacts:contact:create";
    public const string Edit = "contacts:contact:edit";

    [Fact]
    public void PermissionCatalog_ShouldCatalogueTheDeletePermission_ImplyingView()
    {
        var row = PermissionSeeder.GetPermissions().SingleOrDefault(p => p.PermissionId == Delete);

        row.Should().NotBeNull($"{Delete} must be a catalogued id (ADR-0037 §2)");
        row!.Category.Should().Be("contacts");
        row.Resource.Should().Be("contact");
        row.Action.Should().Be("delete");
        row.Description.Should().Be("Delete contacts");
        row.Implies.Should().BeEquivalentTo(["contacts:contact:view"]);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("system_admin")]
    [InlineData("platform_admin")]
    public void TemplatePermissions_ShouldGrantDelete_WhenTheTemplateIsOfTheAdminFamily(string templateId)
    {
        RoleTemplateSeeder.GetTemplatePermissions(templateId).Should().Contain(Delete);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("supervisor")]
    [InlineData("manager")]
    [InlineData("quality_analyst")]
    [InlineData("api")]
    public void TemplatePermissions_ShouldNotGrantDelete_WhenTheTemplateIsNotOfTheAdminFamily(string templateId)
    {
        RoleTemplateSeeder.GetTemplatePermissions(templateId).Should().NotContain(Delete);
    }

    [Fact]
    public void TemplatePermissions_ShouldGrantCreateAndEdit_WhenTheTemplateIsApi()
    {
        RoleTemplateSeeder.GetTemplatePermissions("api").Should().Contain([Create, Edit, "contacts:contact:view"]);
    }

    [Fact]
    public void CanonicalPermissions_ShouldIncludeDelete_WhenTheAdminFamilyIsBuiltFromThem()
    {
        RoleTemplateSeeder.GetCanonicalPermissions().Should().Contain(Delete);
    }
}

/// <summary>
/// contacts-writes-follow-their-permissions task 1.3 against a real Postgres: a tenant whose <c>admin</c> and <c>api</c>
/// roles were cloned before the change gains the new grants at the next <c>MigrateExistingUsersAsync</c>, a grant the
/// tenant added stays, and a second run adds nothing (precedent: <see cref="ConversationReadPermissionMigrationTests"/>).
/// </summary>
[Collection("PostgresRbac")]
public sealed class ContactWritePermissionMigrationTests
{
    private const string Tenant = "acme-before-contact-writes";
    private const string CustomGrant = "queues:queue:view";

    private readonly PostgresRbacFixture _fixture;

    public ContactWritePermissionMigrationTests(PostgresRbacFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SeedRun_ShouldGrantTheClonedAdminAndApiRolesTheNewContactGrants_WhenItRunsAfterTheUpgrade()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await ExecuteAsync(dataSource,
            "INSERT INTO users (user_id, tenant_id, role) VALUES ('user-admin', @T, 2), ('user-api', @T, 3)");
        await SeedAsync(dataSource);
        // The tenant as a release before the change left it: no delete id at all, and api holding view only.
        await ExecuteAsync(dataSource,
            "DELETE FROM tenant_role_permissions WHERE permission_id = 'contacts:contact:delete'; " +
            "DELETE FROM role_template_permissions WHERE permission_id = 'contacts:contact:delete'; " +
            "DELETE FROM permissions WHERE permission_id = 'contacts:contact:delete'; " +
            "DELETE FROM tenant_role_permissions WHERE tenant_id = @T AND role_id = 'api' " +
            "  AND permission_id IN ('contacts:contact:create', 'contacts:contact:edit'); " +
            "DELETE FROM role_template_permissions WHERE template_id = 'api' " +
            "  AND permission_id IN ('contacts:contact:create', 'contacts:contact:edit'); " +
            $"INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id) VALUES (@T, 'api', '{CustomGrant}') " +
            "  ON CONFLICT DO NOTHING");
        (await GrantsAsync(dataSource, "admin")).Should().NotContain(ContactWritePermissionSeedTests.Delete);
        (await GrantsAsync(dataSource, "api")).Should().NotContain([ContactWritePermissionSeedTests.Create, ContactWritePermissionSeedTests.Edit]);

        await SeedAsync(dataSource);

        (await GrantsAsync(dataSource, "admin")).Should().Contain(ContactWritePermissionSeedTests.Delete);
        var api = await GrantsAsync(dataSource, "api");
        api.Should().Contain([ContactWritePermissionSeedTests.Create, ContactWritePermissionSeedTests.Edit, CustomGrant],
            "the start-up copy only adds, so the tenant's own grant stays");
        api.Should().NotContain(ContactWritePermissionSeedTests.Delete);
        var afterFirst = await CountAsync(dataSource);

        await SeedAsync(dataSource);

        (await CountAsync(dataSource)).Should().Be(afterFirst, "a second run adds nothing");
    }

    private static async Task SeedAsync(NpgsqlDataSource dataSource)
    {
        await PermissionSeeder.SeedAsync(dataSource, CancellationToken.None);
        await RoleTemplateSeeder.SeedAsync(dataSource, CancellationToken.None);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<string>> GrantsAsync(NpgsqlDataSource dataSource, string roleId)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT permission_id FROM tenant_role_permissions WHERE tenant_id = @T AND role_id = @R ORDER BY permission_id");
        cmd.Parameters.Add(new NpgsqlParameter("T", Tenant));
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
            "SELECT (SELECT count(*) FROM tenant_role_permissions) + (SELECT count(*) FROM role_template_permissions) " +
            "+ (SELECT count(*) FROM permissions)");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter("T", Tenant));
        await cmd.ExecuteNonQueryAsync();
    }
}
