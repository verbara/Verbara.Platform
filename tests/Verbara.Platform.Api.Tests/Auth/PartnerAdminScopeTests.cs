using System.Security.Claims;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Verbara.Platform.Api.Tests.Auth;

/// <summary>
/// Unit-level regression suite for the Partner-admin scope escalation.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PlatformAdminAuthorizationHandler"/> admitted ANY caller whose tenant was of
/// type <see cref="TenantType.Partner"/> on EVERY <see cref="PlatformAdminRequirement"/>,
/// with no check that the request had anything to do with that Partner's own subtree. The
/// whole <c>/management</c> surface was therefore open to any Partner admin: minting
/// management API keys (which then bypass the tenant boundary entirely), rotating the
/// installation's JWT signing keys, reading the full tenant list, driving cluster and
/// billing.
/// </para>
/// <para>
/// The fix makes the requirement host-tenant-only by default and forces a surface to opt in
/// via <see cref="PlatformAdminRequirement.AllowPartnerDelegation"/> — which it may only do
/// when it resolves a target tenant and checks it against
/// <see cref="TenantHierarchy.IsInCallerHierarchyAsync"/> itself.
/// </para>
/// </remarks>
public sealed class PartnerAdminScopeTests
{
    private const string HostTenantId = "platform";
    private const string PartnerTenantId = "partner-zeta";
    private const string CustomerTenantId = "acme";

    // ─── The escalation itself ───────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldFail_WhenPartnerAdminHitsHostOnlyGate()
    {
        var handler = CreateHandler();
        var ctx = Context(new PlatformAdminRequirement(), TenantAdminPrincipal(PartnerTenantId));

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse(
            because: "a bare PlatformAdminRequirement gates host-only surfaces — /management/api-keys, "
                   + "/management/security/jwt, /management/cluster, the installation tenant list — and a "
                   + "Partner admin has no business on any of them");
    }

    [Fact]
    public async Task HandleAsync_ShouldFail_WhenPartnerAdminHitsPermissionedHostOnlyGate()
    {
        // The permission-gated variant took the same Partner branch, so a Partner admin
        // reached system:retention:manage, system:audit:export, security.jwt.rotate...
        var handler = CreateHandler();
        var ctx = Context(
            new PlatformAdminRequirement(PlatformAdminPermissions.JwtRotate),
            TenantAdminPrincipal(PartnerTenantId));

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse(
            because: "the Partner branch must not be reachable on a host-only permission gate either");
    }

    // ─── The two audited delegations ─────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldSucceed_WhenPartnerAdminHitsDelegatedGate()
    {
        // /management/impersonate — the endpoint resolves the target tenant from the body
        // and enforces the hierarchy itself, so the policy deliberately lets Partners in.
        var handler = CreateHandler();
        var ctx = Context(
            new PlatformAdminRequirement(allowPartnerDelegation: true),
            TenantAdminPrincipal(PartnerTenantId));

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue(
            because: "the delegated gate must keep working or Partners lose impersonation of their own customers");
    }

    [Fact]
    public async Task HandleAsync_ShouldFail_WhenPartnerTenantIsSuspended_OnDelegatedGate()
    {
        var handler = CreateHandler(partnerStatus: TenantStatus.Suspended);
        var ctx = Context(
            new PlatformAdminRequirement(allowPartnerDelegation: true),
            TenantAdminPrincipal(PartnerTenantId));

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse(
            because: "parity with PartnerAdminAuthorizationHandler: a suspended Partner administers nothing");
    }

    // ─── Non-Partner, non-host callers ───────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldFail_WhenCustomerAdminHitsDelegatedGate()
    {
        var handler = CreateHandler();
        var ctx = Context(
            new PlatformAdminRequirement(allowPartnerDelegation: true),
            TenantAdminPrincipal(CustomerTenantId));

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse(
            because: "delegation is to Partners only — a Customer admin is never a platform admin");
    }

    [Fact]
    public async Task HandleAsync_ShouldFail_WhenTenantIsUnknown_OnDelegatedGate()
    {
        var handler = CreateHandler();
        var ctx = Context(
            new PlatformAdminRequirement(allowPartnerDelegation: true),
            TenantAdminPrincipal("tenant-that-does-not-exist"));

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse(because: "an unresolvable tenant fails closed");
    }

    // ─── The host tenant keeps everything ────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldSucceed_WhenHostAdminHitsHostOnlyGate()
    {
        var handler = CreateHandler();
        var ctx = Context(new PlatformAdminRequirement(), TenantAdminPrincipal(HostTenantId));

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue(
            because: "the fix must not touch the host tenant's own reach");
    }

    [Fact]
    public async Task HandleAsync_ShouldSucceed_WhenHostAdminHitsDelegatedGate()
    {
        var handler = CreateHandler();
        var ctx = Context(
            new PlatformAdminRequirement(allowPartnerDelegation: true),
            TenantAdminPrincipal(HostTenantId));

        await handler.HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static AuthorizationHandlerContext Context(
        PlatformAdminRequirement requirement, ClaimsPrincipal principal)
        => new([requirement], principal, resource: null);

    private static PlatformAdminAuthorizationHandler CreateHandler(
        TenantStatus partnerStatus = TenantStatus.Active)
    {
        var host = new Tenant
        {
            TenantId = HostTenantId,
            Name = "Platform Host",
            Status = TenantStatus.Active,
            Type = TenantType.Platform,
            ParentTenantId = null,
        };

        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetHostTenantAsync(Arg.Any<CancellationToken>()).Returns(host);
        tenantStore.GetAsync(HostTenantId, Arg.Any<CancellationToken>()).Returns(host);
        tenantStore.GetAsync(PartnerTenantId, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            TenantId = PartnerTenantId,
            Name = "Partner Zeta",
            Status = partnerStatus,
            Type = TenantType.Partner,
            ParentTenantId = HostTenantId,
        });
        tenantStore.GetAsync(CustomerTenantId, Arg.Any<CancellationToken>()).Returns(new Tenant
        {
            TenantId = CustomerTenantId,
            Name = "Acme Inc",
            Status = TenantStatus.Active,
            Type = TenantType.Customer,
            ParentTenantId = HostTenantId,
        });

        return new PlatformAdminAuthorizationHandler(
            tenantStore, new PermissionResolver(Substitute.For<IUserRoleStore>()));
    }

    /// <summary>
    /// A real-shaped tenant-admin JWT principal: <c>tid</c> + <c>user_id</c> + role
    /// <c>Admin</c>, and deliberately NO <c>key_type</c> — the management-key branch is
    /// covered by <see cref="PlatformAdminAuthorizationHandlerTests"/>.
    /// </summary>
    private static ClaimsPrincipal TenantAdminPrincipal(string tenantId)
        => new(new ClaimsIdentity(
            [
                new Claim("tid", tenantId),
                new Claim("user_id", $"{tenantId}-admin-user"),
                new Claim(ClaimTypes.NameIdentifier, $"{tenantId}-admin-user"),
                new Claim(ClaimTypes.Role, "Admin"),
            ],
            "Bearer"));
}
