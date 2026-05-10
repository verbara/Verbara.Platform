using System.Net;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests.Multitenancy;

/// <summary>
/// Regression suite for the P1 finding <c>PREPUB-2026-05-09-MFA-001</c>:
/// the legacy <c>ResolveTargetTenant</c> in <c>MfaAdminEndpoints</c> trusted any
/// caller-supplied <c>?targetTenant=</c> value. A Partner Admin with the
/// <c>security.mfa.admin</c> permission could therefore reset MFA on, and revoke
/// sessions of, any user whose id existed in an unrelated foreign tenant.
///
/// The fix is <c>ResolveTargetTenantAsync</c>: mirrors the impersonation
/// hierarchy pattern (<c>ManagementImpersonationEndpoints.IsTenantInCallerHierarchyAsync</c>).
/// Allowed iff: (a) caller's own tenant, (b) caller is a Platform-tenant admin,
/// or (c) the override target is a descendant of the caller's tenant. Otherwise
/// returns 403 and emits <c>AuthEventTypes.MfaPrivilegeEscalationAttempted</c>.
/// </summary>
public sealed class MfaAdminCrossTenantTests
    : IClassFixture<CrossTenantHeaderAttackFixture>
{
    private readonly CrossTenantHeaderAttackFixture _fixture;

    public MfaAdminCrossTenantTests(CrossTenantHeaderAttackFixture fixture)
    {
        _fixture = fixture;
    }

    // ─── Attack cases — must be 403 + audit-emit after the fix ───────────────

    [Fact]
    public async Task ResetMfa_ShouldReturn403_WhenPartnerCrossesIntoForeignHierarchy()
    {
        // Partner-Zeta admin tries to reset MFA on a user located inside the
        // unrelated `acme` Customer tenant via ?targetTenant=acme.
        const string victimUserId = "mfa-victim-acme";
        await SeedUserAsync(victimUserId, CrossTenantHeaderAttackFixture.AcmeTenantId, "victim@acme.test", mfaEnabled: true);

        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithTargetTenantQueryAsync(
            client,
            overrideTenant: CrossTenantHeaderAttackFixture.AcmeTenantId,
            path: $"/api/management/mfa/users/{victimUserId}/reset",
            method: HttpMethod.Post);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "MFA-001: a Partner Admin must not be able to reset MFA on a user in an unrelated tenant via ?targetTenant=");

        // Verify the foreign user's MFA state is unchanged.
        using var scope = _fixture.Services.CreateScope();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var refreshed = await userStore.GetByIdAsync(
            new TenantId(CrossTenantHeaderAttackFixture.AcmeTenantId),
            EntityId.From(victimUserId),
            CancellationToken.None);
        refreshed.Should().NotBeNull();
        refreshed!.MfaEnabled.Should().BeTrue("the cross-tenant reset must NOT have wiped MFA state");
    }

    [Fact]
    public async Task ResetMfa_ShouldAllow_WhenPartnerTargetsOwnChild()
    {
        // Partner-Zeta admin resets MFA on a user inside `partner-zeta-customer`
        // (descendant of partner-zeta) — legitimate hierarchy access.
        const string childUserId = "mfa-child-of-partner";
        await SeedUserAsync(
            childUserId,
            CrossTenantHeaderAttackFixture.PartnerZetaCustomerTenantId,
            "child@partner-zeta.test",
            mfaEnabled: true);

        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithTargetTenantQueryAsync(
            client,
            overrideTenant: CrossTenantHeaderAttackFixture.PartnerZetaCustomerTenantId,
            path: $"/api/management/mfa/users/{childUserId}/reset",
            method: HttpMethod.Post);

        response.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            because: "Partner Admin targeting its own descendant tenant must be allowed");
    }

    [Fact]
    public async Task ResetMfa_ShouldAllow_WhenPlatformAdminTargetsAny()
    {
        // Platform admin can reach any tenant — explicit `targetTenant=acme`
        // even though the actor sits on `platform`.
        const string targetUserId = "mfa-platform-target";
        await SeedUserAsync(
            targetUserId,
            CrossTenantHeaderAttackFixture.AcmeTenantId,
            "platform-target@acme.test",
            mfaEnabled: true);

        using var client = _fixture.CreatePlatformAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithTargetTenantQueryAsync(
            client,
            overrideTenant: CrossTenantHeaderAttackFixture.AcmeTenantId,
            path: $"/api/management/mfa/users/{targetUserId}/reset",
            method: HttpMethod.Post);

        response.StatusCode.Should().Be(
            HttpStatusCode.NoContent,
            because: "Platform-tenant admin retains the cross-tenant management capability by design");
    }

    [Fact]
    public async Task RevokeSessions_ShouldReturn403_WhenPartnerCrossesIntoForeignHierarchy()
    {
        const string victimUserId = "mfa-revoke-victim-globex";
        await SeedUserAsync(
            victimUserId,
            CrossTenantHeaderAttackFixture.GlobexTenantId,
            "revoke-victim@globex.test",
            mfaEnabled: true);

        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithTargetTenantQueryAsync(
            client,
            overrideTenant: CrossTenantHeaderAttackFixture.GlobexTenantId,
            path: $"/api/management/mfa/users/{victimUserId}/sessions/revoke",
            method: HttpMethod.Post);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "MFA-001 sister handler: RevokeSessions must enforce the same hierarchy check as ResetMfa");
    }

    // ─── Audit emission for the rejection path ───────────────────────────────

    [Fact]
    public async Task ResetMfa_ShouldEmitAuditEntry_WhenPartnerCrossesIntoForeignHierarchy()
    {
        const string victimUserId = "mfa-audit-victim";
        await SeedUserAsync(victimUserId, CrossTenantHeaderAttackFixture.GlobexTenantId, "audit-victim@globex.test", mfaEnabled: true);

        using var client = _fixture.CreatePartnerZetaAdminClient();

        await CrossTenantHeaderAttackFixture.SendWithTargetTenantQueryAsync(
            client,
            overrideTenant: CrossTenantHeaderAttackFixture.GlobexTenantId,
            path: $"/api/management/mfa/users/{victimUserId}/reset",
            method: HttpMethod.Post);

        using var scope = _fixture.Services.CreateScope();
        var auditStore = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await auditStore.SearchAsync(
            new TenantId(CrossTenantHeaderAttackFixture.PartnerZetaTenantId),
            new AuditQuery(Action: AuthEventTypes.MfaPrivilegeEscalationAttempted, Page: 1, PageSize: 50),
            CancellationToken.None);

        hits.Items.Should().NotBeEmpty(
            "the rejected cross-tenant MFA admin attempt MUST surface in the caller's audit log");
    }

    private async Task SeedUserAsync(string userId, string tenantId, string email, bool mfaEnabled)
    {
        using var scope = _fixture.Services.CreateScope();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
        await userStore.SaveAsync(new User
        {
            UserId = EntityId.From(userId),
            TenantId = new TenantId(tenantId),
            Email = email,
            DisplayName = email,
            Role = UserRole.Agent,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            MfaEnabled = mfaEnabled,
            MfaSecret = mfaEnabled ? "JBSWY3DPEHPK3PXP" : null,
            MfaConfirmedAt = mfaEnabled ? DateTimeOffset.UtcNow : null,
        }, CancellationToken.None);
    }
}
