using System.Net;

namespace Verbara.Platform.Api.Tests.Multitenancy;

/// <summary>
/// End-to-end regressions for OpenSpec change <c>fix-ip-host-tenant-resolution</c>: a host
/// reached by IP address (<c>http://127.0.0.1:5000</c>, the self-host LAN shape) resolved the
/// phantom tenant <c>"127"</c> from the host's first octet, ahead of the <c>X-Tenant-Id</c>
/// header. A Customer principal then got <c>403 "Tenant header does not match authenticated
/// principal."</c> on every call after a successful login; a Platform principal got no 403 at
/// all and silently read under the phantom tenant.
/// </summary>
public sealed class IpHostTenantBoundaryTests : IClassFixture<CrossTenantHeaderAttackFixture>
{
    private readonly CrossTenantHeaderAttackFixture _fixture;

    public IpHostTenantBoundaryTests(CrossTenantHeaderAttackFixture fixture)
    {
        _fixture = fixture;
    }

    // ─── Host-override controls — green before AND after the fix ────────────
    //
    // Prove the overridden Host header actually reaches TenantResolutionMiddleware through the
    // TestServer; without them a green IP-host case below could mean "Host was ignored".

    [Fact]
    public async Task TenantBoundary_ShouldReturn403_WhenHostSubdomainNamesForeignTenant()
    {
        var client = _fixture.CreateAcmeAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHostOverrideAsync(
            client, "globex.platform.example", "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "the overridden Host must reach subdomain resolution and resolve the foreign tenant");
    }

    [Fact]
    public async Task TenantBoundary_ShouldAllow_WhenHostSubdomainNamesOwnTenant()
    {
        var client = _fixture.CreateAcmeAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHostOverrideAsync(
            client, "acme.platform.example", "/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── 4.6 Customer on a bare-IPv4 host — red before the fix ──────────────

    [Theory]
    [InlineData("127.0.0.1:5000")]
    [InlineData("192.168.1.50:5000")]
    public async Task TenantBoundary_ShouldAllow_WhenCustomerSendsOwnTenantHeaderOnIpv4Host(string host)
    {
        var client = _fixture.CreateAcmeAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHostOverrideAsync(
            client, host, "/api/admin/users", headerTenantId: CrossTenantHeaderAttackFixture.AcmeTenantId);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            because: "an IP-address host yields no tenant, so the caller's correct X-Tenant-Id must be honoured");
    }

    // ─── 4.5 (end-to-end) Platform principal on a bare-IPv4 host — red before the fix ─

    [Fact]
    public async Task AdminUsers_ShouldListHeaderTenantUsers_WhenPlatformAdminCallsOnIpv4Host()
    {
        // Before the fix this returns 200 with an EMPTY page: the request is scoped to the
        // phantom tenant "127". The status code alone cannot tell the two apart — the body can.
        var client = _fixture.CreatePlatformAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHostOverrideAsync(
            client, "127.0.0.1:5000", "/api/admin/users", headerTenantId: CrossTenantHeaderAttackFixture.AcmeTenantId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(
            "acme-admin@test.internal",
            because: "the Platform admin named acme in X-Tenant-Id, so acme's users — not tenant \"127\"'s — must be listed");
    }

    // ─── 4.7 A legitimate 403 names what was resolved, and nothing else ─────

    [Fact]
    public async Task TenantBoundary_ShouldNameSubdomainTenantAndSource_WhenHostSubdomainNamesForeignTenant()
    {
        var client = _fixture.CreateAcmeAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHostOverrideAsync(
            client, "globex.platform.example", "/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await ErrorMessageOf(response);
        body.Should().Be("Tenant globex resolved from subdomain does not match authenticated principal.");
        AssertNamesNoOtherTenant(body, except: CrossTenantHeaderAttackFixture.GlobexTenantId);
    }

    [Fact]
    public async Task TenantBoundary_ShouldNameHeaderTenantAndSource_WhenHeaderNamesForeignTenantOnIpv4Host()
    {
        var client = _fixture.CreateAcmeAdminClient();

        var response = await CrossTenantHeaderAttackFixture.SendWithHostOverrideAsync(
            client, "127.0.0.1:5000", "/api/admin/users", headerTenantId: CrossTenantHeaderAttackFixture.GlobexTenantId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await ErrorMessageOf(response);
        body.Should().Be("Tenant globex resolved from header does not match authenticated principal.");
        body.Should().NotContain("127", because: "an IP host contributes no tenant to the diagnosis either");
        AssertNamesNoOtherTenant(body, except: CrossTenantHeaderAttackFixture.GlobexTenantId);
    }

    private static async Task<string> ErrorMessageOf(HttpResponseMessage response)
    {
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetString()!;
    }

    private static void AssertNamesNoOtherTenant(string body, string except)
    {
        string[] tenants =
        [
            CrossTenantHeaderAttackFixture.PlatformTenantId,
            CrossTenantHeaderAttackFixture.AcmeTenantId,
            CrossTenantHeaderAttackFixture.GlobexTenantId,
            CrossTenantHeaderAttackFixture.PartnerZetaTenantId,
            CrossTenantHeaderAttackFixture.PartnerOmegaTenantId,
        ];
        foreach (var tenant in tenants.Where(t => t != except))
            body.Should().NotContain(tenant, because: "the 403 must not enumerate or name any tenant but the resolved one");
    }
}
