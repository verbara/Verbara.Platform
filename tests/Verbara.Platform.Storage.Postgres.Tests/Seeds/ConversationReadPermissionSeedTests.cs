using Npgsql;
using Verbara.Platform.Storage.Postgres.Seeds;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// contact-history-opens-by-assignment task 1.8 (design D16, operator answer Q1; Platform/ADR-0047): the catalogued
/// permission <c>contacts:conversation:read</c> reads every conversation of the tenant with no listening and no action.
/// It is granted to the <c>quality_analyst</c> and <c>api</c> templates and to the admin template family, never to the
/// Agent, Supervisor or Manager templates (Supervisor and Manager read everything through <c>monitor</c>), and an
/// existing tenant's cloned roles gain it at the next seed run, once.
/// </summary>
public sealed class ConversationReadPermissionSeedTests
{
    public const string ReadPermission = "contacts:conversation:read";

    [Fact]
    public void PermissionCatalog_ShouldCatalogueTheReadPermission_WithNoImpliedIds()
    {
        var row = PermissionSeeder.GetPermissions().SingleOrDefault(p => p.PermissionId == ReadPermission);

        row.Should().NotBeNull($"{ReadPermission} must be a catalogued id (ADR-0037 §2)");
        row!.Category.Should().Be("contacts");
        row.Resource.Should().Be("conversation");
        row.Action.Should().Be("read");
        row.Implies.Should().BeEmpty("implication is catalog metadata only, and read implies nothing");
        PermissionSeeder.GetPermissions().Single(p => p.PermissionId == "contacts:conversation:monitor").Implies
            .Should().NotContain(ReadPermission, "monitor's implied list does not change");
    }

    [Theory]
    [InlineData("quality_analyst")]
    [InlineData("api")]
    [InlineData("admin")]
    [InlineData("system_admin")]
    [InlineData("platform_admin")]
    public void TemplatePermissions_ShouldGrantTheReadPermission_WhenTheTemplateReadsEveryConversation(string templateId)
    {
        RoleTemplateSeeder.GetTemplatePermissions(templateId).Should().Contain(ReadPermission);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("supervisor")]
    [InlineData("manager")]
    [InlineData("partner_admin")]
    public void TemplatePermissions_ShouldNotGrantTheReadPermission_WhenTheTemplateDoesNotNeedIt(string templateId)
    {
        RoleTemplateSeeder.GetTemplatePermissions(templateId).Should().NotContain(ReadPermission);
    }

    [Fact]
    public void CanonicalPermissions_ShouldIncludeTheReadPermission_WhenTheAdminFamilyIsBuiltFromThem()
    {
        RoleTemplateSeeder.GetCanonicalPermissions().Should().Contain(ReadPermission);
    }
}

/// <summary>
/// contact-history-opens-by-assignment task 1.8 against a real Postgres: a tenant created before the read permission
/// existed gains it on its cloned <c>quality_analyst</c> and <c>api</c> roles when the platform starts and the RBAC
/// seed runs (<c>RbacMigrationSeeder</c> copies template grants into each tenant role whose id equals the template
/// id), and a second run adds nothing.
/// </summary>
[Collection("PostgresRbac")]
public sealed class ConversationReadPermissionMigrationTests
{
    private const string Tenant = "acme-before-read";

    private readonly PostgresRbacFixture _fixture;

    public ConversationReadPermissionMigrationTests(PostgresRbacFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SeedRun_ShouldGrantTheExistingTenantsClonedQualityAnalystAndApiRolesTheReadPermissionOnce_WhenItRunsAfterTheUpgrade()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await ExecuteAsync(dataSource, "INSERT INTO users (user_id, tenant_id, role) VALUES ('user-qa', @T, 0)", Tenant);
        await SeedAsync(dataSource);
        // The tenant as a release without the permission left it: no catalog row, no template or tenant grant.
        await ExecuteAsync(dataSource,
            "DELETE FROM tenant_role_permissions WHERE permission_id = @P; " +
            "DELETE FROM role_template_permissions WHERE permission_id = @P; " +
            "DELETE FROM permissions WHERE permission_id = @P",
            parameter: ConversationReadPermissionSeedTests.ReadPermission, parameterName: "P");
        (await TenantRolesHoldingReadAsync(dataSource)).Should().BeEmpty("the starting point holds no grant");

        await SeedAsync(dataSource);

        (await TenantRolesHoldingReadAsync(dataSource)).Should().BeEquivalentTo(
            ["quality_analyst", "api", "admin", "system_admin"],
            "the next start copies the new template grants into the tenant's cloned roles");
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

    private static async Task<IReadOnlyList<string>> TenantRolesHoldingReadAsync(NpgsqlDataSource dataSource)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT role_id FROM tenant_role_permissions WHERE tenant_id = @T AND permission_id = @P ORDER BY role_id");
        cmd.Parameters.Add(new NpgsqlParameter("T", Tenant));
        cmd.Parameters.Add(new NpgsqlParameter("P", ConversationReadPermissionSeedTests.ReadPermission));
        var roles = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            roles.Add(reader.GetString(0));
        return roles;
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT (SELECT count(*) FROM tenant_role_permissions) + (SELECT count(*) FROM role_template_permissions) " +
            "+ (SELECT count(*) FROM permissions)");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(
        NpgsqlDataSource dataSource, string sql, string parameter, string parameterName = "T")
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter(parameterName, parameter));
        await cmd.ExecuteNonQueryAsync();
    }
}
