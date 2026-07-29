using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The permission set reported to the client on login and on refresh.
///
/// These exist because the two paths silently diverged: login applied a role-default fallback when
/// the RBAC store resolved nothing, refresh did not. Since the response body drives the frontend's
/// route guards, every refresh stripped a user's permissions mid-session and dropped them on
/// /unauthorized. Measured live before the fix: login returned 57 permissions, refresh returned 0.
/// </summary>
public sealed class EffectivePermissionsTests
{
    [Fact]
    public void EffectivePermissions_ShouldReturnResolved_WhenRbacStoreHasEntries()
    {
        var resolved = new HashSet<string> { "queues:queue:view" };

        var result = AuthEndpoints.EffectivePermissions(resolved, UserRole.Admin);

        result.Should().BeEquivalentTo(["queues:queue:view"]);
    }

    [Fact]
    public void EffectivePermissions_ShouldFallBackToRoleDefaults_WhenResolvedIsNull()
    {
        // null is what a swallowed resolver failure leaves behind.
        var result = AuthEndpoints.EffectivePermissions(null, UserRole.Admin);

        result.Should().NotBeEmpty();
        result.Should().BeEquivalentTo(RoleDefaultPermissions.Admin);
    }

    [Fact]
    public void EffectivePermissions_ShouldFallBackToRoleDefaults_WhenResolvedIsEmpty()
    {
        var result = AuthEndpoints.EffectivePermissions(new HashSet<string>(), UserRole.Admin);

        result.Should().BeEquivalentTo(RoleDefaultPermissions.Admin);
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Supervisor)]
    [InlineData(UserRole.Agent)]
    public void EffectivePermissions_ShouldGrantSomething_WhenPrivilegedRoleHasNoRbacEntries(UserRole role)
    {
        var result = AuthEndpoints.EffectivePermissions(null, role);

        result.Should().NotBeEmpty("a privileged role with no RBAC rows must still be able to render the UI");
    }
}
