using Microsoft.Extensions.Logging;
using Npgsql;
using Verbara.Platform.Storage.Postgres.Seeds;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// harden-rbac-migration-seeder: the boot-time role migration
/// (<see cref="RbacMigrationSeeder.MigrateExistingUsersAsync(NpgsqlDataSource, ILogger, CancellationToken)"/>)
/// contains one tenant's failure inside that tenant's own transaction, logs it and every skipped
/// template clone, and converges when the roles around a user change. A database error is injected with
/// a trigger on <c>tenant_roles</c>, the table the migration writes first, so the failure is a real
/// Postgres error raised mid-tenant.
/// </summary>
[Collection("PostgresRbac")]
public sealed class RbacMigrationResilienceTests
{
    private const int AgentRole = 0;
    private const int AdminRole = 2;
    private const int TemplatesPerTenant = 7;

    private readonly PostgresRbacFixture _fixture;

    public RbacMigrationResilienceTests(PostgresRbacFixture fixture) => _fixture = fixture;

    // ─── One tenant's failure does not abort the others ─────────────────────────

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldMigrateTheOtherTenantsAndLogTheFailure_WhenOneTenantRaisesADatabaseError()
    {
        await using var dataSource = await SeededAsync();
        await GivenUserAsync(dataSource, "a-first", "user-a", AdminRole);
        await GivenUserAsync(dataSource, "b-failing", "user-b", AdminRole);
        await GivenUserAsync(dataSource, "c-third", "user-c", AdminRole);
        var logger = new CapturingLogger();

        await using (await FailingTenantRolesInsertAsync(dataSource, "b-failing", roleId: null))
        {
            var migrate = () => RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, logger, CancellationToken.None);

            await migrate.Should().ThrowAsync<InvalidOperationException>(
                "the migration stays loud in aggregate: after every tenant was tried, one failure is reported " +
                "to the host, which logs it as a failed seed");
        }

        (await CountAsync(dataSource, "SELECT count(*) FROM tenant_roles WHERE tenant_id = 'a-first'")).Should().Be(TemplatesPerTenant);
        (await CountAsync(dataSource, "SELECT count(*) FROM tenant_roles WHERE tenant_id = 'c-third'")).Should().Be(
            TemplatesPerTenant, "the tenant queued behind the failing one is migrated completely");
        (await CountAsync(dataSource, "SELECT count(*) FROM user_roles WHERE tenant_id = 'c-third'")).Should().Be(1);
        (await CountAsync(dataSource, "SELECT count(*) FROM tenant_roles WHERE tenant_id = 'b-failing'")).Should().Be(0);

        var failure = logger.Entries.Should().ContainSingle(e => e.EventId.Id == 3701).Subject;
        failure.Level.Should().Be(LogLevel.Error);
        failure.Message.Should().Contain("b-failing");
        failure.Exception.Should().BeOfType<PostgresException>();
        logger.Entries.Should().ContainSingle(e => e.EventId.Id == 3702)
            .Which.Message.Should().Contain("1 of 3").And.Contain("b-failing");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldRollBackThePartialWritesAndRetryCleanly_WhenATenantFailsPartWay()
    {
        await using var dataSource = await SeededAsync();
        await GivenUserAsync(dataSource, "half", "user-half", AdminRole);

        // 'admin' is the fifth of the seven templates the clone loop visits: four clones land before it fails.
        await using (await FailingTenantRolesInsertAsync(dataSource, "half", roleId: "admin"))
        {
            var migrate = () => RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, new CapturingLogger(), CancellationToken.None);
            await migrate.Should().ThrowAsync<InvalidOperationException>();
        }

        (await CountAsync(dataSource, "SELECT count(*) FROM tenant_roles WHERE tenant_id = 'half'")).Should().Be(
            0, "the four clones made before the failure belong to the same tenant transaction and are rolled back");
        (await CountAsync(dataSource, "SELECT count(*) FROM tenant_role_permissions WHERE tenant_id = 'half'")).Should().Be(0);

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, new CapturingLogger(), CancellationToken.None);

        (await CountAsync(dataSource, "SELECT count(*) FROM tenant_roles WHERE tenant_id = 'half'")).Should().Be(TemplatesPerTenant);
        (await RoleIdsOfAsync(dataSource, "half", "user-half")).Should().Equal("admin");
    }

    // ─── A skipped template clone is recorded ───────────────────────────────────

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldLogTheSkippedCloneWithTheRoleThatCausedIt_WhenTheTenantOwnsAnEquivalentRole()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "demo", "admin-demo", "Admin");
        await GivenUserAsync(dataSource, "demo", "user-demo", AdminRole);
        var logger = new CapturingLogger();

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, logger, CancellationToken.None);

        var skip = logger.Entries.Should().ContainSingle(e => e.EventId.Id == 3700).Subject;
        skip.State.Should().Contain(new KeyValuePair<string, object?>("TenantId", "demo"))
            .And.Contain(new KeyValuePair<string, object?>("TemplateId", "admin"))
            .And.Contain(new KeyValuePair<string, object?>("ExistingRoleId", "admin-demo"));
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldLogNoSkip_WhenTheCloneAlreadyExistsFromAPreviousRun()
    {
        await using var dataSource = await SeededAsync();
        await GivenUserAsync(dataSource, "steady", "user-steady", AdminRole);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, new CapturingLogger(), CancellationToken.None);
        var logger = new CapturingLogger();

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, logger, CancellationToken.None);

        logger.Entries.Should().BeEmpty("a re-run over unchanged data skips nothing worth recording and fails nothing");
    }

    // ─── The resolved user assignment converges across re-runs ──────────────────

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldKeepOneAdminAssignment_WhenTheTenantCreatesTheTemplateIdLater()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "demo", "admin-demo", "Admin");
        await GivenUserAsync(dataSource, "demo", "user-demo", AdminRole);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "demo", "user-demo")).Should().Equal("admin-demo");

        await GivenRoleAsync(dataSource, "demo", "admin", "Administrator");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        (await RoleIdsOfAsync(dataSource, "demo", "user-demo")).Should().Equal(
            ["admin-demo"], "the user already holds the tenant's admin role; the exact template id appearing later must not add a second");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldKeepOneAdminAssignment_WhenTheEquivalentRoleIsRenamed()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "demo", "admin-demo", "Admin");
        await GivenUserAsync(dataSource, "demo", "user-demo", AdminRole);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        await ExecuteAsync(dataSource, "UPDATE tenant_roles SET name = 'Administrators' WHERE tenant_id = 'demo' AND role_id = 'admin-demo'");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        (await RoleIdsOfAsync(dataSource, "demo", "user-demo")).Should().Equal(
            ["admin-demo"], "renaming the role frees the name, so the next run can clone template 'admin', but the user is already settled on the setup admin role");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldKeepOneAdminAssignment_WhenTheSetupRoleGrantedAtSetupIsRenamed()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "demo", "admin-demo", "Admin");
        await GivenUserAsync(dataSource, "demo", "user-demo", AdminRole);
        await GivenAssignmentAsync(dataSource, "demo", "user-demo", "admin-demo", assignedBy: null);

        await ExecuteAsync(dataSource, "UPDATE tenant_roles SET name = 'Administrators' WHERE tenant_id = 'demo' AND role_id = 'admin-demo'");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        (await RoleIdsOfAsync(dataSource, "demo", "user-demo")).Should().Equal(
            ["admin-demo"], "setup grants admin-{tenant} with no assigner; after the rename only its id shape names it, " +
            "and that shape alone settles the user");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldKeepOneAdminAssignment_WhenTheProvisionedRoleIsRenamed()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "acme", "role_admin_acme", "Admin");
        await GivenUserAsync(dataSource, "acme", "user-acme", AdminRole);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal("role_admin_acme");

        await ExecuteAsync(dataSource, "UPDATE tenant_roles SET name = 'Administrators' WHERE tenant_id = 'acme' AND role_id = 'role_admin_acme'");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["role_admin_acme"], "after the rename only the provisioned role_{template}_{tenant} id shape names the role, " +
            "and that shape alone settles the user");
    }

    // ─── Known residual: a role with no template shape loses its match when renamed ─
    // The migration attaches a user to a same-named role under any id, but recognises a held role
    // afterwards only by the template's shapes. Renaming such a role frees the name, the template is
    // cloned, and the next run attaches it too: two admin-equivalent roles, no permission beyond what
    // users.role declares, and stable from then on. A role cloned from a template is recognised by its
    // source template instead (next section), so this stays only for a role with no source template;
    // these tests pin it.

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldAttachTheTemplateRoleAlongsideAndThenHoldSteady_WhenARoleWithAnArbitraryIdTheMigrationGrantedIsRenamed()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "acme", "owners", "Admin");
        await GivenUserAsync(dataSource, "acme", "user-acme", AdminRole);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal("owners");

        await ExecuteAsync(dataSource, "UPDATE tenant_roles SET name = 'Administrators' WHERE tenant_id = 'acme' AND role_id = 'owners'");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["admin", "owners"], "known residual: no template shape names 'owners' after the rename, so the freed name's " +
            "'admin' clone is attached as well; nothing is deleted");

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["admin", "owners"], "the held 'admin' settles the user, so every later run keeps the state the second run reached");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldAttachTheTemplateRoleAlongsideAndThenHoldSteady_WhenARoleWithAnArbitraryIdGrantedAtCreationIsRenamed()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "acme", "owners", "Admin");
        await GivenUserAsync(dataSource, "acme", "user-acme", AdminRole);
        await GivenAssignmentAsync(dataSource, "acme", "user-acme", "owners", "default-role");

        await ExecuteAsync(dataSource, "UPDATE tenant_roles SET name = 'Administrators' WHERE tenant_id = 'acme' AND role_id = 'owners'");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["admin", "owners"], "known residual: a grant made at user creation is recognised by its role's shape only, " +
            "like the migration's own, so the renamed 'owners' does not settle the user");

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["admin", "owners"], "the held 'admin' settles the user, so every later run keeps this state");
    }

    // ─── An administrator's grant is never taken back ───────────────────────────

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldAttachTheDefaultRoleOnceAndKeepTheAdministratorsGrant_WhenTheRoleAnAdministratorGrantedIsRenamed()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "acme", "owners", "Admin");
        await GivenUserAsync(dataSource, "acme", "user-acme", AdminRole);
        await GivenAssignmentAsync(dataSource, "acme", "user-acme", "owners", "some-administrator");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["owners"], "while 'owners' is named like the template it is the tenant's admin role, and the user holds it");

        await ExecuteAsync(dataSource, "UPDATE tenant_roles SET name = 'Administrators' WHERE tenant_id = 'acme' AND role_id = 'owners'");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["admin", "owners"], "an administrator's grant on a role renamed away from the template is that administrator's " +
            "custom role: the migration attaches the default role and deletes nothing");

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["admin", "owners"], "the state the second run reached is the state every later run keeps");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldKeepOneAdminAssignment_WhenAnAdministratorGrantedTheSetupRoleAndTheTemplateIdAppearsLater()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "demo", "admin-demo", "Admin");
        await GivenUserAsync(dataSource, "demo", "user-demo", AdminRole);
        await GivenAssignmentAsync(dataSource, "demo", "user-demo", "admin-demo", "some-administrator");

        await GivenRoleAsync(dataSource, "demo", "admin", "Administrator");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        (await RoleIdsOfAsync(dataSource, "demo", "user-demo")).Should().Equal(
            ["admin-demo"], "whoever granted it, the setup admin role is a shape of template 'admin', so the user is settled");
    }

    // ─── A role cloned from a template is recognised by its source after a rename ─
    // A grant the migration or user creation made follows from users.role. Its role's
    // source_template_id survives a rename, so it still names the UserRole the grant came with when
    // neither the role's id nor its name is a shape of the template any more.

    [Theory]
    [InlineData("agent")]
    [InlineData(null)]
    public async Task MigrateExistingUsersAsync_ShouldAttachTheAdminRole_WhenAnAgentOnARenamedRoleBecameAdminWithoutItsRolesMoving(
        string? sourceTemplateId)
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "acme", "crew", "Agent", sourceTemplateId);
        await GivenUserAsync(dataSource, "acme", "user-acme", AgentRole);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal("crew");

        await ExecuteAsync(dataSource, "UPDATE tenant_roles SET name = 'Crew' WHERE tenant_id = 'acme' AND role_id = 'crew'");
        // users.role written, the role move not made: what a failed move leaves behind.
        await ExecuteAsync(dataSource, $"UPDATE users SET role = {AdminRole} WHERE tenant_id = 'acme' AND user_id = 'user-acme'");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Contain(
            "admin", "the grant on 'crew' came with the user's former role, so it does not settle the user on the new one");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldLeaveOnlyTheAgentRoleInOneRun_WhenAnAdministratorOnTheRenamedSetupRoleBecomesAnAgent()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "t9", "admin-t9", "Admin", sourceTemplateId: "admin");
        await GivenUserAsync(dataSource, "t9", "user-t9", AdminRole);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "t9", "user-t9")).Should().Equal("admin-t9");

        await ExecuteAsync(dataSource, "UPDATE tenant_roles SET name = 'Owners' WHERE tenant_id = 't9' AND role_id = 'admin-t9'");
        await ExecuteAsync(dataSource, $"UPDATE users SET role = {AgentRole} WHERE tenant_id = 't9' AND user_id = 'user-t9'");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        (await RoleIdsOfAsync(dataSource, "t9", "user-t9")).Should().Equal(
            ["agent"], "the migration's grant on the renamed setup role came with the former role, and one run both " +
            "removes it and attaches the agent role");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldKeepOnlyTheRenamedRole_WhenARoleClonedFromTheAdminTemplateTheMigrationGrantedIsRenamed()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "acme", "owners", "Admin", sourceTemplateId: "admin");
        await GivenUserAsync(dataSource, "acme", "user-acme", AdminRole);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal("owners");

        await ExecuteAsync(dataSource, "UPDATE tenant_roles SET name = 'Administrators' WHERE tenant_id = 'acme' AND role_id = 'owners'");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["owners"], "'owners' was cloned from template 'admin', so the user is settled on it whatever it is called");

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal("owners");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldLogTheNameDependentAttachOnce_WhenARoleWithNoSourceTemplateTheMigrationGrantedIsRenamed()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "acme", "owners", "Admin");
        await GivenUserAsync(dataSource, "acme", "user-acme", AdminRole);
        var logger = new CapturingLogger();
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, logger, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal("owners");

        await ExecuteAsync(dataSource, "UPDATE tenant_roles SET name = 'Administrators' WHERE tenant_id = 'acme' AND role_id = 'owners'");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, logger, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["admin", "owners"], "a role with no source template is recognised by its name only, so the renamed 'owners' " +
            "does not settle the user");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, logger, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(["admin", "owners"]);

        var attach = logger.Entries.Should().ContainSingle(e => e.EventId.Id == 3703,
            "the attach that depends on a role's name is recorded when it happens, and only then").Subject;
        attach.Level.Should().Be(LogLevel.Warning);
        attach.State.Should().Contain(new KeyValuePair<string, object?>("TenantId", "acme"))
            .And.Contain(new KeyValuePair<string, object?>("UserId", "user-acme"))
            .And.Contain(new KeyValuePair<string, object?>("RoleId", "admin"))
            .And.Contain(new KeyValuePair<string, object?>("HeldRoleIds", "owners"));
    }

    [Theory]
    [InlineData("role_admin_acme")]
    [InlineData("admin-acme")]
    public async Task MigrateExistingUsersAsync_ShouldKeepOneAdminAssignment_WhenATemplateShapedRoleClonedFromTheAdminTemplateIsRenamed(string roleId)
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "acme", roleId, "Admin", sourceTemplateId: "admin");
        await GivenUserAsync(dataSource, "acme", "user-acme", AdminRole);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(roleId);

        await ExecuteAsync(dataSource, $"UPDATE tenant_roles SET name = 'Administrators' WHERE tenant_id = 'acme' AND role_id = '{roleId}'");
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);
        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            [roleId], "both the id shape and the source template name the role, and they agree");
    }

    // Each row is recognised as a default role of UserRole Admin by one mark only: the template id, one of
    // the id shapes the tenant's copies come in, the template's name, or the source template.
    [Theory]
    [InlineData("admin", "Owners", null)]
    [InlineData("role_admin_t9", "Owners", null)]
    [InlineData("admin-t9", "Owners", null)]
    [InlineData("platform-admin-t9", "Owners", null)]
    [InlineData("owners", "Admin", null)]
    [InlineData("owners", "Owners", "admin")]
    public async Task MigrateExistingUsersAsync_ShouldLeaveOnlyTheAgentRoleInOneRun_WhenTheFormerRoleIsRecognisedByOneMarkAlone(
        string roleId, string name, string? sourceTemplateId)
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "t9", roleId, name, sourceTemplateId);
        await GivenUserAsync(dataSource, "t9", "user-t9", AgentRole);
        await GivenAssignmentAsync(dataSource, "t9", "user-t9", roleId, "default-role");

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        (await RoleIdsOfAsync(dataSource, "t9", "user-t9")).Should().Equal(
            ["agent"], $"'{roleId}' came with the user's former role, and that one mark is enough to take it back in the run " +
            "that attaches the agent role");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldKeepOneAdminAssignment_WhenTheHeldRoleIsNamedLikeTheTemplateAndTheTemplateIdExists()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "acme", "owners", "Admin");
        await GivenRoleAsync(dataSource, "acme", "admin", "Administrator");
        await GivenUserAsync(dataSource, "acme", "user-acme", AdminRole);
        await GivenAssignmentAsync(dataSource, "acme", "user-acme", "owners", "default-role");

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["owners"], "the held role is named like the template, which settles the user although the exact template " +
            "id would be resolved first");
    }

    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldAttachTheAdminRoleAndKeepTheAdministratorsGrant_WhenTheRenamedRoleWasClonedFromTheAdminTemplate()
    {
        await using var dataSource = await SeededAsync();
        await GivenRoleAsync(dataSource, "acme", "owners", "Owners", sourceTemplateId: "admin");
        await GivenUserAsync(dataSource, "acme", "user-acme", AdminRole);
        await GivenAssignmentAsync(dataSource, "acme", "user-acme", "owners", "some-administrator");

        await RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

        (await RoleIdsOfAsync(dataSource, "acme", "user-acme")).Should().Equal(
            ["admin", "owners"], "a role an administrator granted is that administrator's custom role even when it was " +
            "cloned from the admin template, so it does not stand in for the default role and is not taken back");
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

    /// <summary>
    /// Makes every <c>tenant_roles</c> insert for <paramref name="tenantId"/> (and, when given, for
    /// <paramref name="roleId"/> only) raise a Postgres error until the returned handle is disposed.
    /// </summary>
    private static async Task<IAsyncDisposable> FailingTenantRolesInsertAsync(NpgsqlDataSource dataSource, string tenantId, string? roleId)
    {
        var condition = roleId is null
            ? $"NEW.tenant_id = '{tenantId}'"
            : $"NEW.tenant_id = '{tenantId}' AND NEW.role_id = '{roleId}'";
        await ExecuteAsync(dataSource,
            "CREATE OR REPLACE FUNCTION rb1_fail_tenant_roles() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            $"BEGIN IF {condition} THEN RAISE EXCEPTION 'injected failure for %', NEW.tenant_id; END IF; RETURN NEW; END $$; " +
            "CREATE TRIGGER rb1_fail_tenant_roles BEFORE INSERT ON tenant_roles " +
            "FOR EACH ROW EXECUTE FUNCTION rb1_fail_tenant_roles();");
        return new DropTrigger(dataSource);
    }

    private sealed class DropTrigger(NpgsqlDataSource dataSource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await ExecuteAsync(dataSource,
                "DROP TRIGGER IF EXISTS rb1_fail_tenant_roles ON tenant_roles; DROP FUNCTION IF EXISTS rb1_fail_tenant_roles();");
    }

    private static Task GivenUserAsync(NpgsqlDataSource dataSource, string tenantId, string userId, int role) =>
        ExecuteAsync(dataSource,
            "INSERT INTO users (user_id, tenant_id, role) VALUES (@UserId, @TenantId, @Role)",
            new NpgsqlParameter("UserId", userId),
            new NpgsqlParameter("TenantId", tenantId),
            new NpgsqlParameter("Role", role));

    private static Task GivenRoleAsync(
        NpgsqlDataSource dataSource, string tenantId, string roleId, string name, string? sourceTemplateId = null) =>
        ExecuteAsync(dataSource,
            "INSERT INTO tenant_roles (role_id, tenant_id, name, description, source_template_id, is_default) " +
            "VALUES (@RoleId, @TenantId, @Name, 'provisioned outside RbacMigrationSeeder', @SourceTemplateId, false)",
            new NpgsqlParameter("RoleId", roleId),
            new NpgsqlParameter("TenantId", tenantId),
            new NpgsqlParameter("Name", name),
            new NpgsqlParameter("SourceTemplateId", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)sourceTemplateId ?? DBNull.Value });

    private static Task GivenAssignmentAsync(NpgsqlDataSource dataSource, string tenantId, string userId, string roleId, string? assignedBy) =>
        ExecuteAsync(dataSource,
            "INSERT INTO user_roles (tenant_id, user_id, role_id, assigned_at, assigned_by) " +
            "VALUES (@TenantId, @UserId, @RoleId, now(), @AssignedBy)",
            new NpgsqlParameter("TenantId", tenantId),
            new NpgsqlParameter("UserId", userId),
            new NpgsqlParameter("RoleId", roleId),
            new NpgsqlParameter("AssignedBy", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)assignedBy ?? DBNull.Value });

    private static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> RoleIdsOfAsync(NpgsqlDataSource dataSource, string tenantId, string userId)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT role_id FROM user_roles WHERE tenant_id = @TenantId AND user_id = @UserId ORDER BY role_id");
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("UserId", userId));
        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private sealed record LogEntry(
        LogLevel Level, EventId EventId, string Message, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> State);

    private sealed class CapturingLogger : ILogger
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            _entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception, values));
        }
    }
}
