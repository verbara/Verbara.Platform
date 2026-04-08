using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Asterisk.Platform.Core.Branding;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Api.Tests;

/// <summary>
/// Integration tests for GET /api/v1/branding/* — these are public endpoints (no auth).
/// </summary>
public sealed class BrandingEndpointsTests : IClassFixture<PlatformApiFactory>
{
    private readonly HttpClient _client;
    private readonly PlatformApiFactory _factory;

    public BrandingEndpointsTests(PlatformApiFactory factory)
    {
        _factory = factory;
        // No auth headers — these endpoints are intentionally public
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetByTenantId_ShouldReturnBranding_WhenExists()
    {
        // Arrange
        var tenantId = "branding-test-tenant-1";
        var brandingStore = _factory.Services.GetRequiredService<ITenantBrandingStore>();
        await brandingStore.UpsertAsync(new TenantBranding
        {
            TenantId = tenantId,
            DisplayName = "Acme Corp",
            LogoUrl = "https://cdn.acme.io/logo.png",
            FaviconUrl = "https://cdn.acme.io/favicon.ico",
            PrimaryColor = "#0052CC",
            SecondaryColor = "#F4F5F7",
            AccentColor = "#FF5630",
            Locale = "en-US",
            Timezone = "America/New_York",
            SupportEmail = "support@acme.io",
            EmailFromAddress = "no-reply@acme.io",
        });

        // Act
        var response = await _client.GetAsync($"/api/v1/branding/{tenantId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Acme Corp");
        body.Should().Contain("#0052CC");
        body.Should().Contain("America/New_York");
    }

    [Fact]
    public async Task GetBySubdomain_ShouldResolveBranding_WhenSubdomainExists()
    {
        // Arrange
        var tenantId = "branding-test-tenant-2";
        var subdomain = "sprint4-test";
        var brandingStore = _factory.Services.GetRequiredService<ITenantBrandingStore>();
        await brandingStore.UpsertAsync(new TenantBranding
        {
            TenantId = tenantId,
            DisplayName = "Sprint4 Corp",
            PrimaryColor = "#222222",
            Subdomain = subdomain,
        });

        // Act
        var response = await _client.GetAsync($"/api/v1/branding/by-subdomain/{subdomain}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Sprint4 Corp");
        body.Should().Contain("#222222");
    }

    [Fact]
    public async Task GetByTenantId_ShouldReturn404_WhenNotFound()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/branding/nonexistent-tenant-xyz");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetByTenantId_ShouldNotExposeSensitiveFields_WhenReturned()
    {
        // Arrange
        var tenantId = "branding-test-tenant-sensitive";
        var brandingStore = _factory.Services.GetRequiredService<ITenantBrandingStore>();
        await brandingStore.UpsertAsync(new TenantBranding
        {
            TenantId = tenantId,
            DisplayName = "Sensitive Corp",
            SupportEmail = "private-support@example.com",
            EmailFromAddress = "private-from@example.com",
            EmailFromName = "Private From Name",
            SupportUrl = "https://private-support.example.com",
            Subdomain = "sensitive-subdomain",
        });

        // Act
        var response = await _client.GetAsync($"/api/v1/branding/{tenantId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        // Must expose display name
        body.Should().Contain("Sensitive Corp");

        // Must NOT expose sensitive / internal fields
        body.Should().NotContain("private-support@example.com",
            because: "SupportEmail must not be in the public branding response");
        body.Should().NotContain("private-from@example.com",
            because: "EmailFromAddress must not be in the public branding response");
        body.Should().NotContain("Private From Name",
            because: "EmailFromName must not be in the public branding response");
        body.Should().NotContain("private-support.example.com",
            because: "SupportUrl must not be in the public branding response");
        body.Should().NotContain("sensitive-subdomain",
            because: "Subdomain must not be in the public branding response");
    }
}
