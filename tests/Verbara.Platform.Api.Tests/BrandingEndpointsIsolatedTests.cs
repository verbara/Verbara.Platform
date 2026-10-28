using System.Net;
using System.Text.Json;
using Verbara.Platform.Core.Branding;
using Verbara.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Public branding endpoint cases that seed a tenant hierarchy. Each test builds its own
/// <see cref="PlatformApiFactory"/>: it seeds no Platform tenant and the tenant store admits a single
/// one, so each test upserts exactly one root, which a shared fixture would reject on the second test.
/// Only the tenant-status cases send <c>X-Tenant-Id</c>; every other case names no tenant (the test
/// client's host is <c>localhost</c>, a reserved label), which is how it reaches the handler.
/// </summary>
public sealed class BrandingEndpointsIsolatedTests : IDisposable
{
    private static readonly string[] PublicKeys =
    [
        "displayName", "logoUrl", "faviconUrl", "primaryColor",
        "secondaryColor", "accentColor", "locale", "timezone",
    ];

    private readonly PlatformApiFactory _factory;
    private readonly HttpClient _client;

    private readonly string _hostId = "brand-pub-host-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _partnerId = "brand-pub-partner-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _customerId = "brand-pub-customer-" + Guid.NewGuid().ToString("N")[..8];

    public BrandingEndpointsIsolatedTests()
    {
        _factory = new PlatformApiFactory();
        // No auth headers — these endpoints are intentionally public
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GetByTenantId_ShouldReturnPartnerValues_WhenCustomerHasNoRow()
    {
        await SeedHierarchyAsync();
        await UpsertBrandingAsync(new TenantBranding
        {
            TenantId = _partnerId,
            DisplayName = "Partner Brand",
            PrimaryColor = "#FF0000",
        });

        var response = await _client.GetAsync($"/api/v1/branding/{_customerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ParseAsync(response);
        doc.RootElement.GetProperty("displayName").GetString().Should().Be("Partner Brand");
        doc.RootElement.GetProperty("primaryColor").GetString().Should().Be("#FF0000");
    }

    [Fact]
    public async Task GetByTenantId_ShouldReturn404EqualToUnknownId_WhenNoRowInChain()
    {
        await SeedHierarchyAsync();

        var response = await _client.GetAsync($"/api/v1/branding/{_customerId}");
        var unknown = await _client.GetAsync("/api/v1/branding/no-such-tenant");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync())
            .Should().Be(await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetBySubdomain_ShouldInheritEmptyFields_WhenRowFoundBySubdomain()
    {
        await SeedHierarchyAsync();
        await UpsertBrandingAsync(new TenantBranding
        {
            TenantId = _partnerId,
            LogoUrl = "https://cdn.partner.example/logo.png",
        });
        await UpsertBrandingAsync(new TenantBranding
        {
            TenantId = _customerId,
            Subdomain = "acme",
            DisplayName = "Acme",
        });

        var response = await _client.GetAsync("/api/v1/branding/by-subdomain/acme");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ParseAsync(response);
        doc.RootElement.GetProperty("displayName").GetString().Should().Be("Acme");
        doc.RootElement.GetProperty("logoUrl").GetString().Should().Be("https://cdn.partner.example/logo.png");
    }

    [Fact]
    public async Task GetBySubdomain_ShouldReturn404_WhenNoRowCarriesSubdomainEvenIfItNamesATenant()
    {
        await SeedHierarchyAsync();
        await UpsertBrandingAsync(new TenantBranding
        {
            TenantId = _partnerId,
            DisplayName = "Partner Brand",
        });

        // The Customer's id is not read as a subdomain: no row carries it
        var bySubdomain = await _client.GetAsync($"/api/v1/branding/by-subdomain/{_customerId}");
        var byId = await _client.GetAsync($"/api/v1/branding/{_customerId}");

        bySubdomain.StatusCode.Should().Be(HttpStatusCode.NotFound);
        byId.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ParseAsync(byId);
        doc.RootElement.GetProperty("displayName").GetString().Should().Be("Partner Brand");
    }

    [Fact]
    public async Task GetByTenantId_ShouldReturnExactlyThePublicKeys_WhenRowSetsEveryField()
    {
        await SeedHierarchyAsync();
        await UpsertBrandingAsync(new TenantBranding
        {
            TenantId = _customerId,
            DisplayName = "Full Corp",
            LogoUrl = "https://cdn.full.example/logo.png",
            FaviconUrl = "https://cdn.full.example/favicon.ico",
            PrimaryColor = "#111111",
            SecondaryColor = "#222222",
            AccentColor = "#333333",
            Locale = "es-419",
            Timezone = "America/Bogota",
            Subdomain = "full-corp",
            SupportEmail = "support@full.example",
            SupportUrl = "https://support.full.example",
            EmailFromName = "Full Corp Mailer",
            EmailFromAddress = "no-reply@full.example",
        });

        var response = await _client.GetAsync($"/api/v1/branding/{_customerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ParseAsync(response);
        KeysOf(doc).Should().BeEquivalentTo(PublicKeys);
    }

    [Fact]
    public async Task GetByTenantIdAndSubdomain_ShouldReturnOnlyDisplayName_WhenChainSetsNoOtherPublicField()
    {
        await SeedHierarchyAsync();
        await UpsertBrandingAsync(new TenantBranding
        {
            TenantId = _partnerId,
            DisplayName = "Partner Brand",
            Subdomain = "partner-x",
        });

        var byId = await _client.GetAsync($"/api/v1/branding/{_customerId}");
        var bySubdomain = await _client.GetAsync("/api/v1/branding/by-subdomain/partner-x");

        byId.StatusCode.Should().Be(HttpStatusCode.OK);
        bySubdomain.StatusCode.Should().Be(HttpStatusCode.OK);
        using var idDoc = await ParseAsync(byId);
        using var subdomainDoc = await ParseAsync(bySubdomain);
        KeysOf(idDoc).Should().Equal("displayName");
        KeysOf(subdomainDoc).Should().Equal("displayName");
        idDoc.RootElement.GetProperty("displayName").GetString().Should().Be("Partner Brand");
        subdomainDoc.RootElement.GetProperty("displayName").GetString().Should().Be("Partner Brand");
    }

    [Fact]
    public async Task GetByTenantId_ShouldReturnEmptyObject_WhenRowIsBlankInEveryField()
    {
        await SeedHierarchyAsync();
        await UpsertBrandingAsync(new TenantBranding
        {
            TenantId = _customerId,
            DisplayName = "",
            LogoUrl = "",
            FaviconUrl = "",
            PrimaryColor = "",
            SecondaryColor = "",
            AccentColor = "",
            Locale = "",
            Timezone = "",
        });

        var response = await _client.GetAsync($"/api/v1/branding/{_customerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ParseAsync(response);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        KeysOf(doc).Should().BeEmpty();
    }

    [Fact]
    public async Task GetByTenantIdAndSubdomain_ShouldReturn404EqualToUnknownId_WhenTenantIsDeleted()
    {
        await SeedHierarchyAsync();
        await UpsertBrandingAsync(new TenantBranding
        {
            TenantId = _customerId,
            DisplayName = "Old Brand",
            Subdomain = "old-brand",
        });
        await UpdateStatusAsync(_customerId, TenantStatus.Deleted);

        var byId = await _client.GetAsync($"/api/v1/branding/{_customerId}");
        var bySubdomain = await _client.GetAsync("/api/v1/branding/by-subdomain/old-brand");
        var unknown = await _client.GetAsync("/api/v1/branding/no-such-tenant");

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        byId.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await byId.Content.ReadAsStringAsync()).Should().Be(unknownBody);
        bySubdomain.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bySubdomain.Content.ReadAsStringAsync()).Should().Be(unknownBody);
    }

    [Fact]
    public async Task GetByTenantId_ShouldReturnOwnValues_WhenTenantIsSuspendedAndRequestNamesNoTenant()
    {
        await SeedHierarchyAsync();
        await UpsertBrandingAsync(new TenantBranding
        {
            TenantId = _customerId,
            DisplayName = "Paused",
        });
        await UpdateStatusAsync(_customerId, TenantStatus.Suspended);

        var response = await _client.GetAsync($"/api/v1/branding/{_customerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = await ParseAsync(response);
        doc.RootElement.GetProperty("displayName").GetString().Should().Be("Paused");
    }

    [Fact]
    public async Task GetByTenantIdAndSubdomain_ShouldReturn403WithoutBranding_WhenXTenantIdNamesSuspendedTenant()
    {
        await SeedHierarchyAsync();
        await UpsertBrandingAsync(new TenantBranding
        {
            TenantId = _customerId,
            DisplayName = "Paused",
            Subdomain = "paused-sub",
        });
        await UpdateStatusAsync(_customerId, TenantStatus.Suspended);

        var byId = await SendNamingTenantAsync($"/api/v1/branding/{_customerId}", _customerId);
        var bySubdomain = await SendNamingTenantAsync("/api/v1/branding/by-subdomain/paused-sub", _customerId);

        byId.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        bySubdomain.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertNoBrandingFieldAsync(byId);
        await AssertNoBrandingFieldAsync(bySubdomain);
    }

    [Fact]
    public async Task GetByTenantId_ShouldReturnStatus404WithoutBranding_WhenXTenantIdNamesDeletedTenant()
    {
        await SeedHierarchyAsync();
        await UpsertBrandingAsync(new TenantBranding
        {
            TenantId = _customerId,
            DisplayName = "Old Brand",
        });
        await UpdateStatusAsync(_customerId, TenantStatus.Deleted);

        var response = await SendNamingTenantAsync($"/api/v1/branding/{_customerId}", _customerId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // The tenant-status check's 404 carries an error body, unlike the handler's empty one
        (await response.Content.ReadAsStringAsync()).Should().NotBeEmpty();
        await AssertNoBrandingFieldAsync(response);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SeedHierarchyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();

        await tenantStore.UpsertAsync(new Tenant
        {
            TenantId = _hostId,
            Name = "Branding Host",
            Status = TenantStatus.Active,
            Type = TenantType.Platform,
        });
        await tenantStore.UpsertAsync(new Tenant
        {
            TenantId = _partnerId,
            Name = "Branding Partner",
            Status = TenantStatus.Active,
            Type = TenantType.Partner,
            ParentTenantId = _hostId,
        });
        await tenantStore.UpsertAsync(new Tenant
        {
            TenantId = _customerId,
            Name = "Branding Customer",
            Status = TenantStatus.Active,
            Type = TenantType.Customer,
            ParentTenantId = _partnerId,
        });
    }

    // Statuses are set after seeding: the store refuses to delete a tenant with an Active child, so the
    // callers change the Customer, the leaf.
    private async Task UpdateStatusAsync(string tenantId, TenantStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        await tenantStore.UpdateStatusAsync(tenantId, status);
    }

    private async Task UpsertBrandingAsync(TenantBranding branding)
    {
        using var scope = _factory.Services.CreateScope();
        var brandingStore = scope.ServiceProvider.GetRequiredService<ITenantBrandingStore>();
        await brandingStore.UpsertAsync(branding);
    }

    private async Task<HttpResponseMessage> SendNamingTenantAsync(string url, string tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Tenant-Id", tenantId);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static List<string> KeysOf(JsonDocument doc) =>
        doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();

    private static async Task AssertNoBrandingFieldAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Paused").And.NotContain("Old Brand");
        if (body.Length == 0)
            return;

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
            KeysOf(doc).Should().NotIntersectWith(PublicKeys);
    }
}
