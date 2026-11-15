using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// The permission set reported to the client on login and on refresh.
///
/// These exist because the two paths silently diverged: login applied a role-default fallback when
/// the RBAC store resolved nothing, refresh did not. Since the response body drives the frontend's
/// route guards, every refresh stripped a user's permissions mid-session and dropped them on
/// /unauthorized. Measured live before the fix: login returned 57 permissions, refresh returned 0
/// for the same user seconds later.
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

        result.Should().BeEquivalentTo(RoleDefaultPermissions.Admin);
    }

    // contact-history-opens-by-assignment task 1.8 (design D16): the client fallback mirrors the admin template, which
    // now carries contacts:conversation:read (living rbac-permission-vocabulary).
    [Fact]
    public void AdminFallback_ShouldListTheConversationReadPermission_WhenTheAdminTemplateGrantsIt()
    {
        RoleDefaultPermissions.Admin.Should().Contain("contacts:conversation:read");
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

    [Fact]
    public void EffectivePermissions_ShouldReturnEmpty_WhenRoleHasNoDefaults()
    {
        // Api is a machine-to-machine role — it gets no interactive UI defaults.
        var result = AuthEndpoints.EffectivePermissions(null, UserRole.Api);

        result.Should().BeEmpty();
    }

    // The Agent Assist toggle's gate passes only the Platform tenant, so the list the console reads is filtered to
    // match it. The filter runs after the role fallback and on the reported list only; the JWT claim is not touched.
    [Fact]
    public void ReportedPermissions_ShouldDropAgentAssistFeatureManage_WhenTenantIsNotPlatform()
    {
        string[] effective = [PlatformAdminPermissions.AgentAssistFeatureManage, PlatformAdminPermissions.AgentAssistConfigManage, "queues:queue:view"];

        var result = AuthEndpoints.ReportedPermissions(effective, isPlatformTenant: false);

        result.Should().BeEquivalentTo([PlatformAdminPermissions.AgentAssistConfigManage, "queues:queue:view"]);
    }

    [Fact]
    public void ReportedPermissions_ShouldKeepBothAgentAssistIds_WhenTenantIsPlatform()
    {
        string[] effective = [PlatformAdminPermissions.AgentAssistFeatureManage, PlatformAdminPermissions.AgentAssistConfigManage];

        var result = AuthEndpoints.ReportedPermissions(effective, isPlatformTenant: true);

        result.Should().BeEquivalentTo(effective);
    }

    [Fact]
    public void ReportedPermissions_ShouldLeaveTheAdminFallbackUnchanged_WhenTenantIsNotPlatform()
    {
        var effective = AuthEndpoints.EffectivePermissions(null, UserRole.Admin);

        var result = AuthEndpoints.ReportedPermissions(effective, isPlatformTenant: false);

        result.Should().Equal(RoleDefaultPermissions.Admin);
    }

    [Fact]
    public void ReportedPermissions_ShouldReportNothing_WhenTheResolvedSetHoldsOnlyTheHostOnlyIdOutsidePlatform()
    {
        // The filter runs after the fallback: a resolved set must never turn into the role defaults because the
        // one id it held was filtered away.
        var effective = AuthEndpoints.EffectivePermissions(
            new HashSet<string> { PlatformAdminPermissions.AgentAssistFeatureManage }, UserRole.Admin);

        var result = AuthEndpoints.ReportedPermissions(effective, isPlatformTenant: false);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_ShouldReturnRoleDefaultPermissions_WhenRbacStoreResolvesNothing()
    {
        // The regression fence for the actual defect: a successful refresh whose RBAC lookup yields
        // nothing must still report a usable permission set, exactly as login does. Before the fix
        // this returned an empty array and the Web bounced the user to /unauthorized on reload.
        var fixture = new AuthHandlerFixture()
            .WithUser(mfaEnabled: false, role: UserRole.Admin)
            .WithTenantPolicy(new TenantAuthConfig
            {
                TenantId = AuthHandlerFixture.TenantId,
                MfaPolicy = "optional",
            });

        var tokenPair = await fixture.RefreshService.GenerateAsync(
            AuthHandlerFixture.UserId,
            AuthHandlerFixture.TenantId,
            ipAddress: "1.2.3.4",
            userAgent: "Test",
            CancellationToken.None);

        var httpContext = AuthHandlerFixture.BuildHttpContext(refreshCookie: tokenPair.RawToken);

        var result = await AuthEndpoints.Refresh(
            httpContext,
            fixture.UserStore,
            fixture.JwtService,
            fixture.RefreshService,
            fixture.RefreshTokenStore,
            fixture.ConfigStore,
            fixture.MfaPolicyEvaluator,
            fixture.AuthEvents,
            fixture.TenantStore,
            CancellationToken.None);

        var ok = result.Should().BeOfType<Ok<TokenResponse>>().Subject;
        ok.Value!.Permissions.Should().NotBeEmpty(
            "a refresh that reports no permissions strips the Web's route guards mid-session");
        ok.Value.Permissions.Should().BeEquivalentTo(RoleDefaultPermissions.Admin);
    }
}
