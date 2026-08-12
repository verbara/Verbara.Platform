using Npgsql;
using Verbara.Platform.Storage.Postgres.Seeds;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// ADR-0037 — the integration tests that would have caught the partially seeded role model, in both
/// of the places the un-transacted RBAC seeding loop was observed to abort mid-way on a live
/// database: an uncatalogued grant (<c>RoleTemplateSeeder</c>) and a role-name collision
/// (<c>RbacMigrationSeeder</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Defect 1 — orphan grants.</b> Runs the real seeding path (<c>PermissionSeeder.SeedAsync</c>
/// then <see cref="RoleTemplateSeeder.SeedAsync"/>) against a real Postgres carrying the real
/// foreign keys, which is the only place the defect was observable: an id granted by a template but
/// absent from the catalog raises <c>23503</c> on
/// <c>role_template_permissions_permission_id_fkey</c>, and because the seeder inserts row-by-row
/// with no transaction the remainder of the loop never runs. The live database was left with 5 of
/// 11 templates, a partially granted <c>admin</c>, and no partner roles at all.
/// </para>
/// <para>
/// <b>Defect 2 — role-name collisions in the tenant clone loop.</b>
/// <c>RbacMigrationSeeder.MigrateExistingUsersAsync</c> clones the seven per-tenant templates into
/// every tenant that appears in <c>users</c>, and its <c>tenant_roles</c> insert used to conflict-
/// target <c>(tenant_id, role_id)</c> only. But <c>tenant_roles</c> carries a <em>second</em> unique
/// index, <c>idx_tenant_roles_name</c> over <c>(tenant_id, lower(name))</c>: a tenant provisioned
/// outside this seeder can already own an equivalent role under a different id — on the live lab the
/// <c>demo</c> tenant held role_id <c>admin-demo</c> named "Admin" — so cloning template
/// <c>admin</c> missed the conflict target and raised an uncaught <c>23505</c> instead. Same
/// signature as defect 1, same blast radius: <c>demo</c> received 4 of 7 roles (agent, supervisor,
/// quality_analyst, manager) and then died on <c>admin</c>, and the <c>platform</c> tenant — later
/// in the same loop — was never processed at all.
/// </para>
/// <para>
/// <b>The two defects are linked.</b> Defect 1 leaves templates missing from <c>role_templates</c>;
/// the clone loop then silently produces no role for them, and the pre-fix user assignment — a
/// blind <c>VALUES</c> insert of the template id — fired at a role that did not exist and hit
/// <c>user_roles_tenant_id_role_id_fkey</c>. So an aborted template seed detonated the migration
/// seeder on the next boot, by a second and entirely different constraint. The assignment now
/// <em>resolves</em> the role out of <c>tenant_roles</c> (exact id first, same-name fallback,
/// <c>LIMIT 1</c>), which both keeps the FK satisfied by construction and honours what the
/// migration is for: a legacy <c>users.role</c> of Admin should attach to whatever this tenant
/// calls "Admin".
/// </para>
/// <para>
/// Note the deliberate difference from <see cref="RoleTemplateSeederReseedTests"/>: that suite
/// pre-creates the <c>permissions</c> rows from <see cref="RoleTemplateSeeder.GetCanonicalPermissions"/>
/// itself, so the FK is satisfied by construction and the orphan-grant class of defect is invisible
/// to it. Here the catalog comes from the seeder under test.
/// </para>
/// </remarks>
[Collection("PostgresRbac")]
public sealed class RbacSeedIntegrityTests
{
    /// <summary>
    /// The <c>admin</c> template is <c>AllPermissionsExcept</c> these two — the only documented
    /// exclusions (cluster + auth configuration are <c>system_admin</c> territory).
    /// </summary>
    private static readonly string[] AdminExclusions =
    [
        "system:cluster:manage",
        "system:auth:configure",
    ];

    private static readonly string[] ExpectedTemplateIds =
    [
        "agent",
        "supervisor",
        "quality_analyst",
        "manager",
        "admin",
        "system_admin",
        "api",
        "platform_admin",
        "partner_admin",
        "partner_billing",
        "partner_viewer",
    ];

    /// <summary>
    /// The seven templates <c>RbacMigrationSeeder</c> clones into every tenant found in
    /// <c>users</c>, <b>in loop order</b>. The order is load-bearing for the regression assertions:
    /// <c>admin</c> sits fifth, so a clone that throws there takes <c>system_admin</c> and
    /// <c>api</c> down with it — which is precisely the 4-of-7 shape observed live.
    /// </summary>
    private static readonly string[] MigratedTemplateIds =
    [
        "agent",
        "supervisor",
        "quality_analyst",
        "manager",
        "admin",
        "system_admin",
        "api",
    ];

    /// <summary>Tenant that already owns an "Admin"-named role under a non-template id.</summary>
    private const string CollidingTenantId = "demo";

    /// <summary>
    /// The pre-existing role: id <c>admin-demo</c>, name "Admin". Provisioned outside the seeder, so
    /// it collides with template <c>admin</c> on <c>lower(name)</c> but not on <c>role_id</c>.
    /// </summary>
    private const string PreExistingAdminRoleId = "admin-demo";
    private const string PreExistingAdminRoleName = "Admin";

    /// <summary>A second tenant in <c>users</c>, standing in for the live <c>platform</c> tenant.</summary>
    private const string SecondTenantId = "platform";

    /// <summary>
    /// Tenant that holds BOTH the exact template id and a differently-named role matching the
    /// template name, so the assignment resolution has two candidate rows to order.
    /// </summary>
    private const string TieBreakTenantId = "tiebreak";

    /// <summary>Tenant used with a template deliberately absent from <c>role_templates</c>.</summary>
    private const string UnseededTemplateTenantId = "unseeded";

    /// <summary><c>UserRole.Agent</c> as persisted in <c>users.role</c>.</summary>
    private const int AgentRoleEnum = 0;

    /// <summary><c>UserRole.Admin</c> as persisted in <c>users.role</c> — maps to template <c>admin</c>.</summary>
    private const int AdminRoleEnum = 2;

    private readonly PostgresRbacFixture _fixture;

    public RbacSeedIntegrityTests(PostgresRbacFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SeedAsync_ShouldNotThrow_WhenEveryGrantIsCatalogued()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);

        var seed = SeedRbacAsync(dataSource);

        await seed.Should().NotThrowAsync(
            "role_template_permissions has a foreign key onto permissions and RoleTemplateSeeder " +
            "inserts row-by-row without a transaction, so a grant the catalog does not contain " +
            "surfaces here as PostgresException 23503 and silently truncates the role model (ADR-0037)");
    }

    [Fact]
    public async Task SeedAsync_ShouldProduceAllElevenTemplates_WhenSeedingCompletes()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);

        await SeedRbacAsync(dataSource)();

        var templateIds = await ReadColumnAsync(dataSource, "SELECT template_id FROM role_templates");

        templateIds.Should().BeEquivalentTo(ExpectedTemplateIds,
            "seeding aborts on the first orphan grant, so a short template list is the signature " +
            "of a catalog-integrity violation rather than of a missing template definition");
    }

    [Fact]
    public async Task SeedAsync_ShouldGrantAdminEveryCanonicalPermissionExceptItsExclusions_WhenSeedingCompletes()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);

        await SeedRbacAsync(dataSource)();

        var stored = await ReadTemplateGrantsAsync(dataSource, "admin");
        var expected = RoleTemplateSeeder.GetCanonicalPermissions()
            .Where(p => !AdminExclusions.Contains(p, StringComparer.Ordinal))
            .ToArray();

        stored.Should().BeEquivalentTo(expected,
            "the admin template is AllPermissions() minus system:cluster:manage and " +
            "system:auth:configure; the live database held only a partial 67 grants because the " +
            "seed died mid-template");
        stored.Should().NotContain(AdminExclusions,
            "cluster and auth configuration stay system_admin-only");
    }

    [Fact]
    public async Task SeedAsync_ShouldGrantPlatformAdminCreditsGrant_WhenSeedingCompletes()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);

        await SeedRbacAsync(dataSource)();

        var stored = await ReadTemplateGrantsAsync(dataSource, RoleTemplateSeeder.PlatformAdminRoleId);

        stored.Should().NotBeEmpty(
            "platform_admin is the last-but-three template emitted, so it is the first casualty of " +
            "an aborted seed — on the live database it did not exist at all");
        stored.Should().Contain("billing:credits:grant",
            "billing:credits:grant is deliberately outside AllPermissions() and granted only here, " +
            "so it is the id that proves the platform_admin template was seeded in full");
    }

    // ------------------------------------- defect 2: role-name collision in the clone loop

    /// <summary>
    /// The exact live reproduction. Tenant <c>demo</c> already owns <c>admin-demo</c> named "Admin";
    /// cloning template <c>admin</c> (id <c>admin</c>, name "Admin") therefore trips
    /// <c>idx_tenant_roles_name</c>, not the primary key.
    /// </summary>
    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldNotThrow_WhenTheTenantAlreadyOwnsAnEquivalentRoleUnderADifferentId()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await SeedRbacAsync(dataSource)();
        await GivenPreExistingRoleAsync(dataSource, CollidingTenantId, PreExistingAdminRoleId, PreExistingAdminRoleName);
        await GivenUserAsync(dataSource, CollidingTenantId, "user-demo-admin", AdminRoleEnum);

        var migrate = MigrateExistingUsersAsync(dataSource);

        await migrate.Should().NotThrowAsync(
            "tenant_roles is protected by TWO unique constraints — tenant_roles_pkey on " +
            "(tenant_id, role_id) and idx_tenant_roles_name on (tenant_id, lower(name)) — so a " +
            "clone of template 'admin' into a tenant that already holds 'admin-demo' named \"Admin\" " +
            "raises 23505 against the index the ON CONFLICT clause does not target, and the " +
            "migration loop has no transaction and no handler to absorb it");
    }

    /// <summary>
    /// The assertion that actually measures the damage: an abort does not merely skip one role, it
    /// kills the rest of the colliding tenant's clone loop <em>and</em> every tenant queued behind
    /// it. Both halves are asserted in one test on purpose — <c>SELECT DISTINCT tenant_id FROM
    /// users</c> has no ORDER BY, so either tenant may be processed first and either half alone
    /// could pass by luck of the plan. Together they fail on the pre-fix SQL in both orders.
    /// </summary>
    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldCloneEveryRemainingTemplateForBothTenants_WhenOneTenantCollidesOnRoleName()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await SeedRbacAsync(dataSource)();
        await GivenPreExistingRoleAsync(dataSource, CollidingTenantId, PreExistingAdminRoleId, PreExistingAdminRoleName);
        await GivenUserAsync(dataSource, CollidingTenantId, "user-demo-admin", AdminRoleEnum);
        await GivenUserAsync(dataSource, SecondTenantId, "user-platform-admin", AdminRoleEnum);

        await MigrateExistingUsersAsync(dataSource)();

        string[] expectedCollidingRoles =
        [
            .. MigratedTemplateIds.Where(id => !string.Equals(id, "admin", StringComparison.Ordinal)),
            PreExistingAdminRoleId,
        ];
        var collidingRoles = await ReadTenantRoleIdsAsync(dataSource, CollidingTenantId);

        collidingRoles.Should().BeEquivalentTo(expectedCollidingRoles,
            "the colliding clone must be skipped, not fatal: 'admin' is the fifth of seven " +
            "templates, so an abort there costs the tenant 'system_admin' and 'api' as well — the " +
            "live demo tenant ended up with exactly the four templates that precede it. The " +
            "pre-existing 'admin-demo' stays as the tenant's admin role and is deliberately not " +
            "replaced");

        var secondTenantRoles = await ReadTenantRoleIdsAsync(dataSource, SecondTenantId);

        secondTenantRoles.Should().BeEquivalentTo(MigratedTemplateIds,
            "one tenant's pre-existing role naming must not decide whether the NEXT tenant gets an " +
            "RBAC model at all; on the live database the platform tenant was never processed " +
            "because the loop died inside demo");
    }

    /// <summary>
    /// Guards 2 and 3 of the fix, and the point of the whole migration: a legacy <c>users.role</c>
    /// of Admin must end up attached to whatever <em>this</em> tenant calls "Admin".
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the clone is skipped the tenant has no <c>admin</c> role row, so the grant and
    /// assignment inserts that follow would fire against a role_id that does not exist —
    /// <c>tenant_role_permissions_tenant_id_role_id_fkey</c> and
    /// <c>user_roles_tenant_id_role_id_fkey</c> would reject them with <c>23503</c> and abort the
    /// loop a second way. Grants are skipped outright (there is no role to grant to), but the
    /// assignment must <b>resolve</b> rather than skip: leaving the user unassigned would migrate
    /// someone who reads as Admin in <c>users.role</c> into an account holding zero permissions.
    /// </para>
    /// <para>
    /// The <c>agent</c> user is the control: its clone landed normally, so it must still resolve
    /// through the exact-id branch and be unaffected by the name fallback.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldAssignTheTenantsOwnEquivalentRole_WhenTheTemplateCloneWasSkipped()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await SeedRbacAsync(dataSource)();
        await GivenPreExistingRoleAsync(dataSource, CollidingTenantId, PreExistingAdminRoleId, PreExistingAdminRoleName);
        await GivenUserAsync(dataSource, CollidingTenantId, "user-demo-admin", AdminRoleEnum);
        await GivenUserAsync(dataSource, CollidingTenantId, "user-demo-agent", AgentRoleEnum);

        await MigrateExistingUsersAsync(dataSource)();

        var danglingGrants = await ReadDanglingGrantRoleIdsAsync(dataSource, CollidingTenantId);

        danglingGrants.Should().BeEmpty(
            "the grant insert is guarded by EXISTS on the cloned tenant_roles row, so a skipped " +
            "clone must yield no grants for it — without the guard Postgres rejects the insert " +
            "outright with 23503 rather than storing an orphan, killing the loop. Grants cannot " +
            "fall back the way assignments do: the tenant's own 'admin-demo' role carries whatever " +
            "permissions its owner chose, and the migration has no mandate to overwrite them");

        var danglingAssignments = await ReadDanglingAssignmentRoleIdsAsync(dataSource, CollidingTenantId);

        danglingAssignments.Should().BeEmpty(
            "same FK exposure on the user_roles insert (user_roles_tenant_id_role_id_fkey); " +
            "selecting the role out of tenant_roles instead of assuming @RoleId subsumes the " +
            "EXISTS guard, because a query with no candidate row inserts nothing");

        var assignments = await ReadUserRoleAssignmentsAsync(dataSource, CollidingTenantId);

        assignments.Should().Contain($"user-demo-admin/{PreExistingAdminRoleId}",
            "template 'admin' was never cloned into this tenant, so the assignment resolves by " +
            "name onto the equivalent role the tenant already owns. Skipping instead would hand a " +
            "user whose users.role says Admin an account with zero permissions — silently wrong is " +
            "worse than the 23505 it replaced, which at least announced itself");
        assignments.Should().Contain("user-demo-agent/agent",
            "the name fallback must not disturb the normal path: 'agent' cloned cleanly, so the " +
            "exact-id branch resolves it");
        assignments.Should().HaveCount(2,
            "LIMIT 1 means one role per user per legacy enum value — a resolution that matched both " +
            "the exact id and a same-named role must not assign twice");
    }

    /// <summary>
    /// The <c>ORDER BY (tr.role_id = @RoleId) DESC</c> tie-break. Both branches of the resolution
    /// can match distinct rows at once: the tenant holds <c>admin</c> (the template id, but renamed
    /// to "Administrator" by its owner) <em>and</em> <c>admin-demo</c> named "Admin". Note the
    /// scenario is only constructible because the two names differ —
    /// <c>idx_tenant_roles_name</c> forbids two roles sharing a name inside one tenant.
    /// </summary>
    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldPreferTheExactTemplateId_WhenARoleWithTheTemplateNameAlsoExists()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await SeedRbacAsync(dataSource)();
        await GivenPreExistingRoleAsync(dataSource, TieBreakTenantId, "admin", "Administrator");
        await GivenPreExistingRoleAsync(dataSource, TieBreakTenantId, PreExistingAdminRoleId, PreExistingAdminRoleName);
        await GivenUserAsync(dataSource, TieBreakTenantId, "user-tiebreak-admin", AdminRoleEnum);

        await MigrateExistingUsersAsync(dataSource)();

        var assignments = await ReadUserRoleAssignmentsAsync(dataSource, TieBreakTenantId);

        assignments.Should().BeEquivalentTo(["user-tiebreak-admin/admin"],
            "when both branches match, the exact template id must win over the name fallback: the " +
            "fallback exists only to cover the id being absent. Postgres orders false before true, " +
            "so ORDER BY (tr.role_id = @RoleId) DESC puts the exact match first and LIMIT 1 takes " +
            "it — without the DESC the tenant's unrelated 'admin-demo' would be assigned instead");
    }

    /// <summary>
    /// The edge the removed EXISTS guard used to cover: neither the template's <c>role_id</c> nor a
    /// same-named role exists for the tenant.
    /// </summary>
    /// <remarks>
    /// This is not hypothetical — it is the state defect 1 left behind. When
    /// <see cref="RoleTemplateSeeder.SeedAsync"/> aborted on an uncatalogued grant, <c>admin</c> and
    /// everything after it never reached <c>role_templates</c> at all. The clone loop then no-ops
    /// (its <c>SELECT … FROM role_templates</c> returns nothing), the name subquery resolves to
    /// <c>NULL</c>, and the pre-fix blind <c>VALUES</c> insert fired at a nonexistent role — so
    /// defect 1 detonated defect 2 on the very next boot. The resolution form must stay inert here.
    /// </remarks>
    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldAssignNothingAndNotThrow_WhenNeitherTheTemplateIdNorASameNamedRoleExists()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await SeedRbacAsync(dataSource)();
        await GivenTemplateRemovedAsync(dataSource, "admin");
        await GivenUserAsync(dataSource, UnseededTemplateTenantId, "user-orphan-admin", AdminRoleEnum);
        await GivenUserAsync(dataSource, UnseededTemplateTenantId, "user-orphan-agent", AgentRoleEnum);

        var migrate = MigrateExistingUsersAsync(dataSource);

        await migrate.Should().NotThrowAsync(
            "with no 'admin' row in role_templates there is nothing to clone and nothing to resolve " +
            "by name, so the assignment must simply select zero rows; the pre-fix VALUES insert " +
            "instead pushed a nonexistent role_id at user_roles_tenant_id_role_id_fkey and took the " +
            "remaining tenants down with it");

        var assignments = await ReadUserRoleAssignmentsAsync(dataSource, UnseededTemplateTenantId);

        assignments.Should().BeEquivalentTo(["user-orphan-agent/agent"],
            "only the admin user is unresolvable — every template that DID survive must still be " +
            "cloned and assigned, which is the whole point of not aborting");

        var tenantRoles = await ReadTenantRoleIdsAsync(dataSource, UnseededTemplateTenantId);

        tenantRoles.Should().BeEquivalentTo(
            MigratedTemplateIds.Where(id => !string.Equals(id, "admin", StringComparison.Ordinal)),
            "the missing template is skipped, not fatal: system_admin and api sit after admin in " +
            "the clone loop and must still land");
    }

    /// <summary>
    /// The seeder runs on every boot against a database that already ran it, so a second pass over
    /// unchanged data must write nothing — including in the colliding shape, where the second pass
    /// re-attempts the same conflicting clone.
    /// </summary>
    [Fact]
    public async Task MigrateExistingUsersAsync_ShouldChangeNothing_WhenRunASecondTime()
    {
        await _fixture.ResetAsync();
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await SeedRbacAsync(dataSource)();
        await GivenPreExistingRoleAsync(dataSource, CollidingTenantId, PreExistingAdminRoleId, PreExistingAdminRoleName);
        await GivenUserAsync(dataSource, CollidingTenantId, "user-demo-admin", AdminRoleEnum);
        await GivenUserAsync(dataSource, CollidingTenantId, "user-demo-agent", AgentRoleEnum);
        await GivenUserAsync(dataSource, SecondTenantId, "user-platform-admin", AdminRoleEnum);

        await MigrateExistingUsersAsync(dataSource)();
        var afterFirst = await ReadRbacRowCountsAsync(dataSource);

        var secondRun = MigrateExistingUsersAsync(dataSource);

        await secondRun.Should().NotThrowAsync(
            "every insert in the migration is ON CONFLICT DO NOTHING, so a re-run over already " +
            "migrated data must be inert rather than raise a duplicate-key error");

        var afterSecond = await ReadRbacRowCountsAsync(dataSource);

        afterSecond.Should().BeEquivalentTo(afterFirst,
            "a second pass that adds or removes rows would mean the seeder is not idempotent and " +
            "each boot would mutate a tenant's role model");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The production seeding order, minus <c>RbacMigrationSeeder</c> (which needs the users table
    /// the RBAC fixture only recently grew). Kept migration-free so the ADR-0037 orphan-grant tests
    /// above continue to exercise nothing but the catalog and the templates.
    /// </summary>
    private static Func<Task> SeedRbacAsync(NpgsqlDataSource dataSource) => async () =>
    {
        await PermissionSeeder.SeedAsync(dataSource, CancellationToken.None);
        await RoleTemplateSeeder.SeedAsync(dataSource, CancellationToken.None);
    };

    /// <summary>The third stage of <c>RbacSeederOrchestrator.SeedRbacAsync</c>, under test on its own.</summary>
    private static Func<Task> MigrateExistingUsersAsync(NpgsqlDataSource dataSource) => () =>
        RbacMigrationSeeder.MigrateExistingUsersAsync(dataSource, CancellationToken.None);

    /// <summary>
    /// Inserts a <c>tenant_roles</c> row the clone loop did not put there — the out-of-band
    /// provisioning (or later renaming) the seeder has to tolerate. <c>source_template_id</c> is
    /// left null, matching how custom roles look in the live database; the resolution SQL keys off
    /// <c>role_id</c> and <c>name</c> only, so the column plays no part either way.
    /// </summary>
    private static Task GivenPreExistingRoleAsync(
        NpgsqlDataSource dataSource, string tenantId, string roleId, string name) =>
        ExecuteAsync(dataSource,
            "INSERT INTO tenant_roles (role_id, tenant_id, name, description, source_template_id, is_default) " +
            "VALUES (@RoleId, @TenantId, @Name, 'provisioned outside RbacMigrationSeeder', NULL, false)",
            new NpgsqlParameter("RoleId", roleId),
            new NpgsqlParameter("TenantId", tenantId),
            new NpgsqlParameter("Name", name));

    /// <summary>
    /// Inserts the only thing <c>MigrateExistingUsersAsync</c> reads out of <c>users</c>: the tenant
    /// that makes the loop visit it, and the integer <c>UserRole</c> the assignment maps from.
    /// </summary>
    private static Task GivenUserAsync(
        NpgsqlDataSource dataSource, string tenantId, string userId, int role) =>
        ExecuteAsync(dataSource,
            "INSERT INTO users (user_id, tenant_id, role) VALUES (@UserId, @TenantId, @Role)",
            new NpgsqlParameter("UserId", userId),
            new NpgsqlParameter("TenantId", tenantId),
            new NpgsqlParameter("Role", role));

    /// <summary>
    /// Deletes a template row, reproducing the state defect 1 left on the live database: the
    /// un-transacted <see cref="RoleTemplateSeeder.SeedAsync"/> loop aborted before emitting it.
    /// <c>role_template_permissions</c> cascades; <c>tenant_roles.source_template_id</c> has no FK.
    /// </summary>
    private static Task GivenTemplateRemovedAsync(NpgsqlDataSource dataSource, string templateId) =>
        ExecuteAsync(dataSource,
            "DELETE FROM role_templates WHERE template_id = @TemplateId",
            new NpgsqlParameter("TemplateId", templateId));

    /// <summary>
    /// Grants whose <c>role_id</c> has no <c>tenant_roles</c> row. The FK makes storing one
    /// impossible, so a non-empty result is unreachable — the assertion documents that the insert
    /// must be <em>skipped</em>, since the alternative is Postgres raising 23503 and killing the loop.
    /// </summary>
    private static Task<List<string>> ReadDanglingGrantRoleIdsAsync(
        NpgsqlDataSource dataSource, string tenantId) =>
        ReadColumnAsync(
            dataSource,
            "SELECT DISTINCT trp.role_id FROM tenant_role_permissions trp " +
            "WHERE trp.tenant_id = @TenantId AND NOT EXISTS (" +
            "  SELECT 1 FROM tenant_roles tr " +
            "  WHERE tr.tenant_id = trp.tenant_id AND tr.role_id = trp.role_id)",
            new NpgsqlParameter("TenantId", tenantId));

    /// <summary>Assignments whose <c>role_id</c> has no <c>tenant_roles</c> row; see above.</summary>
    private static Task<List<string>> ReadDanglingAssignmentRoleIdsAsync(
        NpgsqlDataSource dataSource, string tenantId) =>
        ReadColumnAsync(
            dataSource,
            "SELECT DISTINCT ur.role_id FROM user_roles ur " +
            "WHERE ur.tenant_id = @TenantId AND NOT EXISTS (" +
            "  SELECT 1 FROM tenant_roles tr " +
            "  WHERE tr.tenant_id = ur.tenant_id AND tr.role_id = ur.role_id)",
            new NpgsqlParameter("TenantId", tenantId));

    private static Task<List<string>> ReadTenantRoleIdsAsync(NpgsqlDataSource dataSource, string tenantId) =>
        ReadColumnAsync(
            dataSource,
            "SELECT role_id FROM tenant_roles WHERE tenant_id = @TenantId",
            new NpgsqlParameter("TenantId", tenantId));

    /// <summary>Assignments as <c>user_id/role_id</c> so a single string column carries the pair.</summary>
    private static Task<List<string>> ReadUserRoleAssignmentsAsync(NpgsqlDataSource dataSource, string tenantId) =>
        ReadColumnAsync(
            dataSource,
            "SELECT user_id || '/' || role_id FROM user_roles WHERE tenant_id = @TenantId",
            new NpgsqlParameter("TenantId", tenantId));

    /// <summary>Row counts of every table the migration writes, for the idempotency comparison.</summary>
    private static async Task<Dictionary<string, long>> ReadRbacRowCountsAsync(NpgsqlDataSource dataSource) =>
        new(StringComparer.Ordinal)
        {
            ["tenant_roles"] = await CountAsync(dataSource, "SELECT COUNT(*) FROM tenant_roles"),
            ["tenant_role_permissions"] = await CountAsync(dataSource, "SELECT COUNT(*) FROM tenant_role_permissions"),
            ["user_roles"] = await CountAsync(dataSource, "SELECT COUNT(*) FROM user_roles"),
        };

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        return (long?)await cmd.ExecuteScalarAsync() ?? 0L;
    }

    private static async Task ExecuteAsync(
        NpgsqlDataSource dataSource, string sql, params NpgsqlParameter[] parameters)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
            cmd.Parameters.Add(parameter);

        await cmd.ExecuteNonQueryAsync();
    }

    private static Task<List<string>> ReadTemplateGrantsAsync(NpgsqlDataSource dataSource, string templateId) =>
        ReadColumnAsync(
            dataSource,
            "SELECT permission_id FROM role_template_permissions WHERE template_id = @TemplateId",
            new NpgsqlParameter("TemplateId", templateId));

    private static async Task<List<string>> ReadColumnAsync(
        NpgsqlDataSource dataSource, string sql, params NpgsqlParameter[] parameters)
    {
        var rows = new List<string>();
        await using var cmd = dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
            cmd.Parameters.Add(parameter);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));

        return rows;
    }
}
