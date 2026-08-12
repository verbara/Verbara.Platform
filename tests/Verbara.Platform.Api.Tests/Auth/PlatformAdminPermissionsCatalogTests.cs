using Verbara.Platform.Api.Auth;
using Verbara.Platform.Storage.Postgres.Seeds;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// ADR-0037 catalog-integrity guard (gate side).
/// </summary>
/// <remarks>
/// <para>
/// <c>PermissionResolver.HasPermission</c> is an exact set-membership test — there is no alias,
/// prefix or implication layer at check time. A <see cref="PlatformAdminRequirement"/> naming an id
/// that <c>PermissionSeeder</c> never catalogues can therefore never be satisfied on its permission,
/// and the surface stays reachable only through the <c>Admin</c>/<c>SystemAdmin</c> role shortcut in
/// <see cref="PlatformAdminAuthorizationHandler"/>. Six admin surfaces sat in exactly that state —
/// silently — until ADR-0037.
/// </para>
/// <para>
/// The gate ids are held as constants in <see cref="PlatformAdminPermissions"/> rather than as
/// string literals at the policy site precisely so this assertion can enumerate them.
/// </para>
/// </remarks>
public sealed class PlatformAdminPermissionsCatalogTests
{
    [Fact]
    public void All_ShouldBeCatalogued_WhenComparedToPermissionSeeder()
    {
        var offenders = PlatformAdminPermissions.All
            .Where(id => !PermissionCatalog.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.Should().BeEmpty(
            "every permission id named by a PlatformAdminRequirement gate must exist in the catalog " +
            "produced by PermissionSeeder (ADR-0037); an uncatalogued gate id can never be satisfied " +
            "by any principal. Uncatalogued gate ids: [" + string.Join(", ", offenders) + "]");
    }

    [Fact]
    public void All_ShouldContainEveryDeclaredGateConstant_WhenEnumerated()
    {
        // Guards the guard: a gate constant added to PlatformAdminPermissions but omitted from All
        // would slip past the catalog assertion above.
        PlatformAdminPermissions.All.Should().BeEquivalentTo(
            new[]
            {
                PlatformAdminPermissions.MfaManage,
                PlatformAdminPermissions.AuditView,
                PlatformAdminPermissions.AuditExport,
                PlatformAdminPermissions.ImpersonationManage,
                PlatformAdminPermissions.RetentionView,
                PlatformAdminPermissions.RetentionManage,
                PlatformAdminPermissions.JwtRotate,
                PlatformAdminPermissions.CreditsGrant,
            },
            "PlatformAdminPermissions.All is the enumeration the catalog-integrity guard walks; a " +
            "constant missing from it is an ungoverned gate id");
    }
}
