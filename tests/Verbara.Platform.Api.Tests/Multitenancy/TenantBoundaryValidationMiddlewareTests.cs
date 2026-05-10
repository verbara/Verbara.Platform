using System.Net;
using System.Net.Http.Headers;

namespace Verbara.Platform.Api.Tests.Multitenancy;

/// <summary>
/// Regression suite for the P0 finding <c>PREPUB-2026-05-09-MT-001</c>:
/// any tenant Admin holding a valid JWT can scope their request to a foreign
/// Customer tenant by appending an <c>X-Tenant-Id: victim</c> header. Because
/// <c>TenantResolutionMiddleware</c> resolves the header BEFORE
/// <c>UseAuthentication</c> runs, and the JWT handler's <c>OnTokenValidated</c>
/// only sets the tenant when <c>Items["TenantId"]</c> is empty, the bare
/// <c>AdminOnly</c> surfaces (<c>/admin/users|queues|agents|teams|audit</c>)
/// trust the header-resolved tenant unconditionally and return another
/// tenant's data.
///
/// The fix is <c>TenantBoundaryValidationMiddleware</c>, registered after
/// <c>UseAuthorization</c>, which rejects with 403 when the principal's
/// <c>tid</c>/<c>tenant_id</c> claim does not match
/// <c>Items["TenantId"]</c> — UNLESS:
/// <list type="bullet">
///   <item><description>the principal carries
///   <c>key_type=management</c> (legitimate cross-tenant management surface;
///   pinned to the separate ADMIN-002 follow-up),</description></item>
///   <item><description>the principal's tenant resolves to
///   <c>TenantType.Platform</c> or <c>TenantType.Partner</c> (legitimate
///   cross-tenant administration of customer tenants).</description></item>
/// </list>
/// </summary>
public sealed class TenantBoundaryValidationMiddlewareTests
    : IClassFixture<CrossTenantHeaderAttackFixture>
{
    private readonly CrossTenantHeaderAttackFixture _fixture;

    public TenantBoundaryValidationMiddlewareTests(CrossTenantHeaderAttackFixture fixture)
    {
        _fixture = fixture;
    }

    // ─── Attack cases — must be 403 after the fix ────────────────────────────

    [Fact]
    public async Task TenantBoundary_ShouldReturn403_WhenAdminCallsForeignTenantViaHeader_OnAdminUsers()
    {
        // Acme admin tries to read Globex's user directory via header.
        var client = _fixture.CreateAcmeAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.GlobexTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "MT-001: a Customer Admin must not be able to scope /admin/users to a foreign tenant via X-Tenant-Id");
    }

    [Fact]
    public async Task TenantBoundary_ShouldReturn403_WhenAdminCallsForeignTenantViaHeader_OnAdminQueues()
    {
        var client = _fixture.CreateAcmeAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.GlobexTenantId, "/api/admin/queues");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TenantBoundary_ShouldReturn403_WhenAdminCallsForeignTenantViaHeader_OnAdminAgents()
    {
        var client = _fixture.CreateAcmeAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.GlobexTenantId, "/api/admin/agents");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TenantBoundary_ShouldReturn403_WhenAdminCallsForeignTenantViaHeader_OnAdminAudit()
    {
        // /admin/audit is the legacy audit query surface — same exposure pattern
        // as /admin/users etc.
        var client = _fixture.CreateAcmeAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.GlobexTenantId, "/api/admin/audit/");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── Legitimate-use control cases — must stay 200 after the fix ──────────

    [Fact]
    public async Task TenantBoundary_ShouldAllow_WhenPlatformAdminUsesHeader()
    {
        // Platform admin operating on a customer tenant via header is the
        // canonical legitimate cross-tenant administration pattern.
        var client = _fixture.CreatePlatformAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.AcmeTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "Platform-tenant admins must retain header-driven cross-tenant access");
    }

    [Fact]
    public async Task TenantBoundary_ShouldAllow_WhenPartnerAdminTargetsOwnCustomer()
    {
        // Partner admin reaches into a child customer tenant via header — the
        // current model treats Partner-type tenants as authorized to manage
        // descendants. The middleware enforces tenant-TYPE not parent-chain
        // here (parent-chain enforcement lives at the per-handler level for
        // hierarchy-sensitive surfaces; defense-in-depth via TenantType is
        // sufficient for the bare AdminOnly group).
        var client = _fixture.CreatePartnerZetaAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.PartnerZetaCustomerTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "Partner-type admins must continue to manage their child customer tenants via header");
    }

    [Fact]
    public async Task TenantBoundary_ShouldAllow_WhenManagementApiKeyUsed()
    {
        // The management API key is intentionally allowed to bypass the
        // boundary check (the broader scope-aware enforcement is tracked in
        // ADMIN-002, separate Phase 2 follow-up). Pinning current behavior
        // so we do not regress the management surface as a side effect of
        // the MT-001 fix.
        var client = _fixture.CreateManagementApiKeyClient();
        // Note: API keys overwrite Items["TenantId"] in
        // ApiKeyAuthenticationHandler (the key.TenantId wins over the header),
        // so the header is effectively ignored regardless of the boundary
        // middleware. Either way, the request must succeed.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
        request.Headers.Add("X-Tenant-Id", CrossTenantHeaderAttackFixture.AcmeTenantId);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "management API keys must reach AdminOnly surfaces (scope-aware enforcement is ADMIN-002)");
    }

    [Fact]
    public async Task TenantBoundary_ShouldAllow_WhenNoHeaderPresent_AndJwtTidUsed()
    {
        // Default path: no header, JWT.tid drives the request. Must remain
        // unaffected by the fix.
        var client = _fixture.CreateAcmeAdminClient();

        var response = await client.GetAsync("/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "the default tenant-by-JWT path must continue to succeed");
    }

    // ─── Self-tenant header (degenerate, must allow) ─────────────────────────

    [Fact]
    public async Task TenantBoundary_ShouldAllow_WhenHeaderMatchesJwtTid()
    {
        var client = _fixture.CreateAcmeAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CrossTenantHeaderAttackFixture.AcmeTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "self-tenant header is a no-op; must not be punished by the boundary check");
    }

    // ─── Test infrastructure: silence CS0618-style sanity checks ─────────────

    [Fact]
    public void AuthorizationHeader_ShouldBeBearer_OnFixtureClients()
    {
        // Sanity: the fixture mints JWTs and sets Bearer scheme. If this ever
        // breaks (e.g. a fixture refactor sneaks in), every test above silently
        // returns 401 — a false-green pattern. Pin the wire format here.
        var client = _fixture.CreateAcmeAdminClient();
        var auth = client.DefaultRequestHeaders.Authorization;
        auth.Should().NotBeNull();
        auth!.Scheme.Should().Be("Bearer");
        auth.Parameter.Should().NotBeNullOrEmpty();
        auth.Parameter!.Should().StartWith("eyJ", because: "JWTs always start with the JOSE header eyJ");
    }
}
