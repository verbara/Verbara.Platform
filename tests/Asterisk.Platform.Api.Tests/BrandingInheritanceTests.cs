using System.Net;
using System.Net.Http.Json;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Branding;
using Asterisk.Sdk.Pro.MultiTenant;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Asterisk.Platform.Api.Tests;

public sealed class BrandingInheritanceTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;
    private readonly PlatformAdminApiFactory _factory;

    private readonly string _partnerId = "branding-partner-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _customerId = "branding-customer-" + Guid.NewGuid().ToString("N")[..8];

    public BrandingInheritanceTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreatePlatformAdminClient();

        // Seed Partner → Customer hierarchy
        using var scope = factory.Services.CreateScope();
        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();

        tenantStore.UpsertAsync(new Tenant
        {
            TenantId = _partnerId,
            Name = "Branding Partner",
            Status = TenantStatus.Active,
            Type = TenantType.Partner,
            ParentTenantId = PlatformAdminApiFactory.HostTenantId,
        }).AsTask().GetAwaiter().GetResult();

        tenantStore.UpsertAsync(new Tenant
        {
            TenantId = _customerId,
            Name = "Branding Customer",
            Status = TenantStatus.Active,
            Type = TenantType.Customer,
            ParentTenantId = _partnerId,
        }).AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CustomerInheritance_ShouldInheritFromPartner_WhenOwnBrandingIsNull()
    {
        // Seed partner branding only
        using var scope = _factory.Services.CreateScope();
        var brandingStore = scope.ServiceProvider.GetRequiredService<ITenantBrandingStore>();
        await brandingStore.UpsertAsync(new TenantBranding
        {
            TenantId = _partnerId,
            PrimaryColor = "#FF0000",
            DisplayName = "Partner Brand",
        });

        // GET customer settings → should inherit from partner
        var response = await _client.GetAsync($"/api/v1/management/tenants/{_customerId}/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"primaryColor\":\"#FF0000\"");
        body.Should().Contain("\"displayName\":\"Partner Brand\"");
    }

    [Fact]
    public async Task CustomerOverride_ShouldUseOwnValue_WhenSet()
    {
        // Seed partner branding
        using var scope = _factory.Services.CreateScope();
        var brandingStore = scope.ServiceProvider.GetRequiredService<ITenantBrandingStore>();
        await brandingStore.UpsertAsync(new TenantBranding
        {
            TenantId = _partnerId,
            PrimaryColor = "#FF0000",
        });

        // Seed customer branding with override
        await brandingStore.UpsertAsync(new TenantBranding
        {
            TenantId = _customerId,
            PrimaryColor = "#00FF00",
        });

        // GET customer settings → should use own value
        var response = await _client.GetAsync($"/api/v1/management/tenants/{_customerId}/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"primaryColor\":\"#00FF00\"");
    }

    [Fact]
    public async Task SubdomainShouldNotInherit_WhenParentHasSubdomain()
    {
        // Seed partner branding with subdomain
        using var scope = _factory.Services.CreateScope();
        var brandingStore = scope.ServiceProvider.GetRequiredService<ITenantBrandingStore>();
        await brandingStore.UpsertAsync(new TenantBranding
        {
            TenantId = _partnerId,
            Subdomain = "partner-x",
            PrimaryColor = "#0000FF",
        });

        // GET customer settings → subdomain should NOT inherit, but color should
        var response = await _client.GetAsync($"/api/v1/management/tenants/{_customerId}/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"primaryColor\":\"#0000FF\"");
        // Subdomain should NOT inherit — it must be absent (null-omitted) or explicitly null
        body.Should().NotContain("\"subdomain\":\"partner-x\"");
    }
}
