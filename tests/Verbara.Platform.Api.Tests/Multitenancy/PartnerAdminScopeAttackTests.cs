using System.Net;
using System.Net.Http.Json;
using Verbara.Platform.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Api.Tests.Multitenancy;

/// <summary>
/// End-to-end regressions for the Partner-admin scope escalation.
/// </summary>
/// <remarks>
/// <para>
/// Two independent holes, both keyed on tenant TYPE where the parent chain was the thing
/// that mattered:
/// </para>
/// <list type="number">
///   <item><description><c>PlatformAdminAuthorizationHandler</c> admitted any Partner-tenant
///   caller on every <c>PlatformAdminRequirement</c>, handing a Partner admin the whole
///   <c>/management</c> surface across the installation — including
///   <c>POST /management/api-keys</c>, which mints a management key, and a management key
///   skips the tenant-boundary middleware outright.</description></item>
///   <item><description><c>TenantBoundaryValidationMiddleware</c> let any Partner-tenant
///   caller name any tenant in <c>X-Tenant-Id</c>, so Partner A read and wrote Partner B's
///   customers on the bare <c>/admin/*</c> surfaces.</description></item>
/// </list>
/// <para>
/// The attacker here is always <c>partner-zeta</c>'s admin — a real, paying Partner with a
/// valid token, not an outsider. That is what makes it a scope bug rather than an
/// authentication bug.
/// </para>
/// </remarks>
public sealed class PartnerAdminScopeAttackTests
    : IClassFixture<CrossTenantHeaderAttackFixture>
{
    private static readonly string[] EscalationScopes = ["platform:*"];

    private readonly CrossTenantHeaderAttackFixture _fixture;

    public PartnerAdminScopeAttackTests(CrossTenantHeaderAttackFixture fixture)
    {
        _fixture = fixture;
    }

    // ─── Hole 2: X-Tenant-Id across the partner boundary ─────────────────────

    [Fact]
    public async Task AdminUsers_ShouldReturn403_WhenPartnerTargetsRivalPartnersCustomer()
    {
        // The commercially interesting case: BPO Zeta reads BPO Omega's client directory.
        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.PartnerOmegaCustomerTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "a Partner may reach into its OWN subtree only — partner-omega-customer descends "
                   + "from partner-omega, not from partner-zeta");
    }

    [Fact]
    public async Task AdminUsers_ShouldReturn403_WhenPartnerTargetsRivalPartnerItself()
    {
        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.PartnerOmegaTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "a sibling Partner is not a descendant either");
    }

    [Fact]
    public async Task AdminUsers_ShouldReturn403_WhenPartnerTargetsPlatformParentedCustomer()
    {
        // acme hangs off the platform host, not off partner-zeta.
        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.AcmeTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "being a Partner is not itself authority over a tenant the Partner does not own");
    }

    [Fact]
    public async Task AdminQueues_ShouldReturn403_WhenPartnerTargetsRivalPartnersCustomer()
    {
        // A second surface, because the middleware is the shared gate: if it is wrong it is
        // wrong for every bare AdminOnly route, not just /admin/users.
        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.PartnerOmegaCustomerTenantId, "/api/admin/queues");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminUsers_ShouldAllow_WhenPartnerTargetsItsOwnCustomer()
    {
        // The control case the fix must NOT break: partner-zeta-customer's parent IS
        // partner-zeta, so the reach is legitimate.
        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.PartnerZetaCustomerTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "the parent chain authorises this one, and Partners must keep managing their own clients");
    }

    [Fact]
    public async Task AdminUsers_ShouldAllow_WhenPlatformAdminTargetsAnyTenant()
    {
        using var client = _fixture.CreatePlatformAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.PartnerOmegaCustomerTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "the host tenant operates the whole installation and is untouched by this fix");
    }

    // ─── Hole 1: the /management surface ─────────────────────────────────────

    [Fact]
    public async Task CreateManagementApiKey_ShouldReturn403_WhenCallerIsPartnerAdmin()
    {
        // The worst of the set. A management key bypasses TenantBoundaryValidationMiddleware
        // by design, so minting one converts a Partner admin into an unbounded operator —
        // and it survives the Partner's own contract being terminated.
        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await client.PostAsJsonAsync(
            "/api/management/api-keys",
            new { name = "escalation", scopes = EscalationScopes });

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "minting a management key is host-tenant work; a Partner doing it escapes every tenant boundary");
    }

    [Theory]
    [InlineData("/api/management/api-keys")]
    [InlineData("/api/management/tenants")]
    [InlineData("/api/management/security/jwt/keys")]
    [InlineData("/api/management/cluster/nodes")]
    [InlineData("/api/management/rate-cards")]
    [InlineData("/api/management/invoices")]
    [InlineData("/api/management/retention/targets")]
    [InlineData("/api/management/webhooks/dead-letter")]
    [InlineData("/api/management/gdpr/purge-log")]
    public async Task ManagementSurface_ShouldReturn403_WhenCallerIsPartnerAdmin(string path)
    {
        // None of these resolve a target tenant from the request, so none of them could ever
        // have been scoped to the caller's subtree: reaching them at all is the escalation.
        // /management/tenants is listed here deliberately — it enumerates every tenant in the
        // installation, rival Partners included.
        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: $"{path} is a host-tenant surface; a Partner's own work lives under /partner/*");
    }

    [Fact]
    public async Task ManagementTenants_ShouldNotReturn403_WhenCallerIsPlatformAdmin()
    {
        // Control: the host tenant must still reach the surfaces the Partner just lost.
        using var client = _fixture.CreatePlatformAdminClient();

        var response = await client.GetAsync("/api/management/tenants");

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            because: "the fix narrows Partner reach, not the host tenant's");
    }

    // ─── The delegated surfaces must actually be scoped ──────────────────────

    [Fact]
    public async Task MfaUserList_ShouldReturn403_WhenPartnerNamesARivalsTenant()
    {
        // The MFA group is one of the two that may admit a Partner, on the promise that each of
        // its handlers resolves the target tenant and checks the hierarchy. The list verb did
        // not: it forwarded ?tenant= straight into the query.
        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await client.GetAsync(
            $"/api/management/mfa/users?tenant={CrossTenantHeaderAttackFixture.PartnerOmegaCustomerTenantId}");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "reading a rival's user directory is the same escalation as resetting their MFA, "
                   + "and must be refused and audited the same way");
    }

    [Fact]
    public async Task MfaUserList_ShouldReturnOnlyOwnTenant_WhenPartnerPassesNoFilter()
    {
        // Worse than the named-rival case: an omitted filter meant "every active tenant", so one
        // unadorned GET returned every user's email, MFA state and last login in the installation.
        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await client.GetAsync("/api/management/mfa/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(
            CrossTenantHeaderAttackFixture.PartnerOmegaTenantId,
            because: "an unfiltered listing must not fan out across the installation for a non-host caller");
        body.Should().NotContain(CrossTenantHeaderAttackFixture.AcmeTenantId);
        body.Should().NotContain("globex-admin@test.internal");
    }

    [Fact]
    public async Task MfaUserList_ShouldAllow_WhenPartnerNamesItsOwnCustomer()
    {
        using var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await client.GetAsync(
            $"/api/management/mfa/users?tenant={CrossTenantHeaderAttackFixture.PartnerZetaCustomerTenantId}");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "the parent chain authorises this one — and the console sends ?tenant=, so the "
                   + "parameter must keep working, only scoped");
    }

    [Fact]
    public async Task MfaUserList_ShouldStillFanOut_WhenCallerIsPlatformAdmin()
    {
        // The control case: the host tenant's installation-wide view is the reason the unfiltered
        // fan-out exists at all, and the fix must not take it away.
        using var client = _fixture.CreatePlatformAdminClient();

        var response = await client.GetAsync("/api/management/mfa/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(
            CrossTenantHeaderAttackFixture.AcmeTenantId,
            because: "the host tenant still sees every tenant");
    }

    // ─── The blast radius of the delegation flag ─────────────────────────────

    [Fact]
    public void PartnerDelegatedPolicies_ShouldGateOnlyTheSurfacesThatCheckHierarchyThemselves()
    {
        // A future /management group that wires the delegated policy without resolving a
        // target tenant re-opens the hole quietly. This pins the whole opted-in set, read
        // from the live endpoint table rather than from source, so a rename cannot hide it.
        var delegated = new HashSet<string>(StringComparer.Ordinal)
        {
            PlatformAdminRequirement.PartnerDelegatedPolicy,
            Verbara.Platform.Api.Endpoints.Mfa.MfaAdminEndpoints.AuthorizationPolicy,
        };

        using var scope = _fixture.Services.CreateScope();
        var routes = scope.ServiceProvider
            .GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(a => a.Policy is { } p && delegated.Contains(p)))
            .Select(e => e.RoutePattern.RawText!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        routes.Should().BeEquivalentTo(
            [
                "/api/v{version:apiVersion}/management/impersonate",
                "/api/v{version:apiVersion}/management/mfa/users",
                "/api/v{version:apiVersion}/management/mfa/users/{id}/reset",
                "/api/v{version:apiVersion}/management/mfa/users/{id}/sessions/revoke",
            ],
            because: "these are the only routes that resolve a target tenant and check it against "
                   + "TenantHierarchy themselves; anything else opting into Partner delegation is the bug again. "
                   + "Adding a route here is a claim about that route — the MfaUserList_* tests above exist "
                   + "because this list once named a route that made no such check");
    }
}
