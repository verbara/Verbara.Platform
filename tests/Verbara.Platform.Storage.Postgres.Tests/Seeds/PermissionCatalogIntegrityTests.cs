using Verbara.Platform.Storage.Postgres.Seeds;

namespace Verbara.Platform.Storage.Postgres.Tests.Seeds;

/// <summary>
/// ADR-0037 catalog-integrity guard (grant side), pure unit — no Postgres required.
/// </summary>
/// <remarks>
/// <para>
/// <c>role_template_permissions.permission_id</c> carries a foreign key onto <c>permissions</c>, and
/// <see cref="RoleTemplateSeeder.SeedAsync"/> inserts row-by-row with <b>no transaction</b>. A single
/// grant whose id <see cref="PermissionSeeder"/> never catalogues therefore raises <c>23503</c> and
/// aborts the remainder of the seeding loop, leaving a <em>partially</em> seeded role model rather
/// than a failed one. That is exactly how the live database ended up with 5 of 11 templates, a
/// partially granted <c>admin</c> and zero partner roles across 103 tenants.
/// </para>
/// <para>
/// These assertions make that class of defect a build failure. The failure message names the
/// offending ids — a bare "expected true but found false" would not have told anyone what broke.
/// </para>
/// </remarks>
public sealed class PermissionCatalogIntegrityTests
{
    /// <summary>The eleven canonical role templates emitted by <see cref="RoleTemplateSeeder"/>.</summary>
    public static TheoryData<string> CanonicalTemplateIds => new()
    {
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
    };

    [Fact]
    public void AllPermissions_ShouldBeCatalogued_WhenComparedToPermissionSeeder()
    {
        var offenders = Uncatalogued(RoleTemplateSeeder.GetCanonicalPermissions());

        offenders.Should().BeEmpty(Because("RoleTemplateSeeder.AllPermissions()", offenders));
    }

    [Theory]
    [MemberData(nameof(CanonicalTemplateIds))]
    public void TemplatePermissions_ShouldBeCatalogued_WhenTemplateIsCanonical(string templateId)
    {
        var permissions = RoleTemplateSeeder.GetTemplatePermissions(templateId);

        permissions.Should().NotBeEmpty(
            $"role template '{templateId}' must exist in RoleTemplateSeeder and grant at least one permission");

        var offenders = Uncatalogued(permissions);

        offenders.Should().BeEmpty(Because($"role template '{templateId}'", offenders));
    }

    [Fact]
    public void PermissionCatalog_ShouldNotBeEmpty_WhenProjectedFromPermissionSeeder()
    {
        // Guards the guards: if PermissionCatalog ever projected an empty set the membership
        // assertions above would still pass vacuously for a template with no grants, and would
        // otherwise fail for the wrong reason.
        PermissionCatalog.Ids.Should().NotBeEmpty(
            "PermissionCatalog projects PermissionSeeder.GetPermissions(); an empty projection " +
            "would make every catalog-integrity assertion meaningless");
    }

    private static string[] Uncatalogued(IEnumerable<string> permissionIds) =>
    [
        .. permissionIds
            .Where(id => !PermissionCatalog.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    private static string Because(string source, IReadOnlyCollection<string> offenders) =>
        $"every permission id granted by {source} must exist in the catalog produced by " +
        "PermissionSeeder (ADR-0037): role_template_permissions' FK onto permissions aborts the " +
        "un-transacted seeding loop with 23503 on the first orphan grant. Uncatalogued ids: [" +
        string.Join(", ", offenders) + "]";
}
