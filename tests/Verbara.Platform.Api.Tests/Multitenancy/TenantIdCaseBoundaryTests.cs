using System.Net;
using System.Net.Http.Json;

namespace Verbara.Platform.Api.Tests.Multitenancy;

/// <summary>
/// A tenant id that differs only in case is a different tenant. The store keys tenant ids exactly, so every gate that
/// decides "is this the caller's tenant" or "is this the host" must compare them exactly too.
/// </summary>
public sealed class TenantIdCaseBoundaryTests : IClassFixture<CaseVariantTenantFixture>
{
    private readonly CaseVariantTenantFixture _fixture;

    public TenantIdCaseBoundaryTests(CaseVariantTenantFixture fixture)
    {
        _fixture = fixture;
    }

    // ─── The tenant boundary ─────────────────────────────────────────────────

    [Theory]
    [InlineData(CaseVariantTenantFixture.UpperTenantId, CaseVariantTenantFixture.LowerTenantId)]
    [InlineData(CaseVariantTenantFixture.LowerTenantId, CaseVariantTenantFixture.UpperTenantId)]
    public async Task TenantBoundary_ShouldReturn403_WhenHeaderNamesTenantDifferingOnlyInCase(string own, string other)
    {
        var client = _fixture.CreateAdminClient(own);

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(client, other, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: $"tenant '{other}' is a different tenant from '{own}', whatever the case");
    }

    [Fact]
    public async Task TenantBoundary_ShouldReturn403_WhenSubdomainNamesTenantDifferingOnlyInCase()
    {
        var client = _fixture.CreateAdminClient(CaseVariantTenantFixture.UpperTenantId);

        var response = await CrossTenantHeaderAttackFixture.SendWithHostOverrideAsync(
            client, "acme.platform.example", "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "the subdomain resolves tenant 'acme', which is not the caller's tenant 'ACME'");
    }

    [Theory]
    [InlineData(CaseVariantTenantFixture.UpperTenantId)]
    [InlineData(CaseVariantTenantFixture.LowerTenantId)]
    public async Task TenantBoundary_ShouldAllow_WhenHeaderNamesOwnTenantExactly(string own)
    {
        var client = _fixture.CreateAdminClient(own);

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(client, own, "/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(CaseVariantTenantFixture.UpperTenantId)]
    [InlineData(CaseVariantTenantFixture.LowerTenantId)]
    public async Task TenantBoundary_ShouldAllow_WhenHostAdminNamesEitherTenant(string target)
    {
        var client = _fixture.CreateAdminClient(CaseVariantTenantFixture.HostTenantId);

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(client, target, "/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TenantBoundary_ShouldAllow_WhenPartnerNamesOwnCustomer()
    {
        var client = _fixture.CreateAdminClient(CaseVariantTenantFixture.PartnerTenantId);

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CaseVariantTenantFixture.PartnerCustomerTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TenantBoundary_ShouldReturn403_WhenCaseVariantOfPartnerNamesPartnersCustomer()
    {
        var client = _fixture.CreateAdminClient(CaseVariantTenantFixture.PartnerVariantTenantId);

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CaseVariantTenantFixture.PartnerCustomerTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "'PARTNER-K' is a Customer, not the Partner 'partner-k'");
    }

    [Fact]
    public async Task TenantBoundary_ShouldReturn403_WhenCaseVariantOfHostNamesAnotherTenant()
    {
        var client = _fixture.CreateAdminClient(CaseVariantTenantFixture.HostVariantTenantId);

        var response = await CrossTenantHeaderAttackFixture.SendWithHeaderOverrideAsync(
            client, CaseVariantTenantFixture.LowerTenantId, "/api/admin/users");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "'PLATFORM' is a Customer, not the host tenant 'platform'");
    }

    // ─── The Platform-administrator gate ─────────────────────────────────────

    [Fact]
    public async Task PlatformAdminGate_ShouldReturn403_WhenCallerTenantDiffersFromHostOnlyInCase()
    {
        var client = _fixture.CreateAdminClient(CaseVariantTenantFixture.HostVariantTenantId);

        var response = await client.GetAsync("/api/management/tenants");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "only the host tenant's exact id is the host");
    }

    [Fact]
    public async Task PlatformAdminGate_ShouldAllow_WhenCallerIsHostTenant()
    {
        var client = _fixture.CreateAdminClient(CaseVariantTenantFixture.HostTenantId);

        var response = await client.GetAsync("/api/management/tenants");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── Creating a child tenant ─────────────────────────────────────────────

    [Fact]
    public async Task CreateTenant_ShouldCreateChildUnderAnyParent_WhenCallerIsHostTenantExactly()
    {
        var client = _fixture.CreateAdminClient(CaseVariantTenantFixture.HostTenantId);
        var childId = "case-child-" + Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = childId,
            name = "Case child",
            type = 2, // Customer
            parentTenantId = CaseVariantTenantFixture.PartnerTenantId,
        });

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            because: "the host tenant may create a child under a Partner it does not belong to");
    }

    [Fact]
    public async Task CreateTenant_ShouldReturn403_WhenCallerTenantDiffersFromHostOnlyInCase()
    {
        var client = _fixture.CreateAdminClient(CaseVariantTenantFixture.HostVariantTenantId);
        var childId = "case-child-" + Guid.NewGuid().ToString("N")[..8];

        var response = await client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = childId,
            name = "Case child",
            type = 2, // Customer
            parentTenantId = CaseVariantTenantFixture.PartnerTenantId,
        });

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            because: "'PLATFORM' is a Customer, not the host tenant 'platform'");
    }
}
