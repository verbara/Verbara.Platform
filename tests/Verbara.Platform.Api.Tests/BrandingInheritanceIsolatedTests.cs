using System.Net;
using System.Text.Json;
using Verbara.Platform.Core.Branding;
using Verbara.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// Settings-view branding cases that seed the Platform root's own row, or need no row anywhere in the
/// chain. Each test builds its own <see cref="PlatformAdminApiFactory"/>: the tenant store admits a
/// single Platform tenant and is a singleton per factory, so a row on
/// <see cref="PlatformAdminApiFactory.HostTenantId"/> in a shared fixture would leak into every later
/// case of <see cref="BrandingInheritanceTests"/>.
/// </summary>
public sealed class BrandingInheritanceIsolatedTests : IDisposable
{
    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    private readonly string _partnerId = "branding-iso-partner-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _customerId = "branding-iso-customer-" + Guid.NewGuid().ToString("N")[..8];

    public BrandingInheritanceIsolatedTests()
    {
        _factory = new PlatformAdminApiFactory();
        _client = _factory.CreatePlatformAdminClient();

        // Seed Platform (the factory's HostTenantId) → Partner → Customer
        using var scope = _factory.Services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();

        tenantStore.UpsertAsync(new Tenant
        {
            TenantId = _partnerId,
            Name = "Isolated Branding Partner",
            Status = TenantStatus.Active,
            Type = TenantType.Partner,
            ParentTenantId = PlatformAdminApiFactory.HostTenantId,
        }).AsTask().GetAwaiter().GetResult();

        tenantStore.UpsertAsync(new Tenant
        {
            TenantId = _customerId,
            Name = "Isolated Branding Customer",
            Status = TenantStatus.Active,
            Type = TenantType.Customer,
            ParentTenantId = _partnerId,
        }).AsTask().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task CustomerInheritance_ShouldReachPlatform_WhenPartnerFieldIsNull()
    {
        // Platform sets the support URL; the Partner has a row but no support URL; the Customer has no row
        using var scope = _factory.Services.CreateScope();
        var brandingStore = scope.ServiceProvider.GetRequiredService<ITenantBrandingStore>();
        await brandingStore.UpsertAsync(new TenantBranding
        {
            TenantId = PlatformAdminApiFactory.HostTenantId,
            SupportUrl = "https://support.licensee.example",
        });
        await brandingStore.UpsertAsync(new TenantBranding
        {
            TenantId = _partnerId,
            DisplayName = "Partner Brand",
        });

        var response = await _client.GetAsync($"/api/v1/management/tenants/{_customerId}/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var branding = doc.RootElement.GetProperty("branding");
        branding.GetProperty("supportUrl").GetString().Should().Be("https://support.licensee.example");
        branding.GetProperty("displayName").GetString().Should().Be("Partner Brand");
    }

    [Fact]
    public async Task Settings_ShouldKeepBrandingKeyWithAllFieldsNull_WhenNoRowInChain()
    {
        // No branding row on the Platform, the Partner or the Customer
        var response = await _client.GetAsync($"/api/v1/management/tenants/{_customerId}/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("branding", out var branding).Should().BeTrue(
            "an all-null branding DTO keeps the key; only a null DTO would be dropped by WhenWritingNull");
        branding.ValueKind.Should().Be(JsonValueKind.Object);
        branding.EnumerateObject()
            .Where(p => p.Value.ValueKind != JsonValueKind.Null)
            .Select(p => p.Name)
            .Should().BeEmpty();
    }
}
