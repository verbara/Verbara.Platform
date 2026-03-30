using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class ManagementTenantEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public ManagementTenantEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task ListTenants_ShouldRequirePlatformAdmin()
    {
        using var factory = new PlatformApiFactory();
        var anonClient = factory.CreateClient();

        var response = await anonClient.GetAsync("/api/management/tenants");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListTenants_ShouldReturnHostTenant()
    {
        var response = await _client.GetAsync("/api/management/tenants");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("platform");
    }

    [Fact]
    public async Task CreateTenant_ShouldCreateChildOfHost()
    {
        var response = await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "test-child-" + Guid.NewGuid().ToString("N")[..8],
            name = "Test Child Tenant",
            type = 2, // Customer
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateTenant_ShouldRejectDepthViolation()
    {
        // Create a partner
        var partnerId = "partner-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = partnerId,
            name = "Test Partner",
            type = 1, // Partner
        });

        // Create a customer under partner
        var customerId = "cust-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = customerId,
            name = "Test Customer",
            type = 2, // Customer
            parentTenantId = partnerId,
        });

        // Try to create a child under the customer — should fail (depth > 3)
        var response = await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "too-deep-" + Guid.NewGuid().ToString("N")[..8],
            name = "Too Deep",
            type = 2,
            parentTenantId = customerId,
        });

        // Customer type requires Platform or Partner parent
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SuspendTenant_ShouldUpdateStatus()
    {
        var tenantId = "suspend-test-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId,
            name = "Suspend Test",
            type = 2,
        });

        var response = await _client.PostAsync($"/api/management/tenants/{tenantId}/suspend", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Suspended");
    }

    [Fact]
    public async Task SuspendTenant_ShouldRejectPlatformTenant()
    {
        var response = await _client.PostAsync("/api/management/tenants/platform/suspend", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteTenant_ShouldSoftDelete()
    {
        var tenantId = "delete-test-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId,
            name = "Delete Test",
            type = 2,
        });

        var response = await _client.DeleteAsync($"/api/management/tenants/{tenantId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
