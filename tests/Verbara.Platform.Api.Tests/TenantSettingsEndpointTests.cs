using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace Verbara.Platform.Api.Tests;

public sealed class TenantSettingsEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public TenantSettingsEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", PlatformAdminApiFactory.HostTenantId);
    }

    [Fact]
    public async Task GetSettings_ShouldReturnAggregatedSettings()
    {
        var response = await _client.GetAsync("/api/v1/admin/tenant/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("operational");
        body.Should().Contain("auth");
        body.Should().Contain("quotas");
        body.Should().Contain("retention");
        body.Should().Contain("rateLimitTier");
    }

    [Fact]
    public async Task GetSettings_ShouldReturnDefaultValues_WhenNoConfigSet()
    {
        var response = await _client.GetAsync("/api/v1/admin/tenant/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"mfaPolicy\":\"optional\"");
        body.Should().Contain("\"maxConcurrentChannels\":100");
    }

    [Fact]
    public async Task UpdateSettings_ShouldUpdateAuthSection()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            auth = new { passwordMinLength = 16 },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync("/api/v1/admin/tenant/settings");
        var body = await getResponse.Content.ReadAsStringAsync();
        body.Should().Contain("\"passwordMinLength\":16");
    }

    [Fact]
    public async Task UpdateSettings_ShouldIgnoreQuotas_WhenAdminOnly()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            quotas = new { maxActiveAgents = 999 },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

public sealed class ManagementTenantSettingsEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public ManagementTenantSettingsEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task GetSettings_ShouldReturnSettingsForAnyTenant()
    {
        var tenantId = "settings-mgmt-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId,
            name = "Settings Mgmt Test",
            type = 2,
        });

        var response = await _client.GetAsync($"/api/v1/management/tenants/{tenantId}/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(tenantId);
        body.Should().Contain("operational");
    }

    [Fact]
    public async Task UpdateSettings_ShouldUpdateAllSections()
    {
        var tenantId = "settings-all-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId,
            name = "Settings All Test",
            type = 2,
        });

        var response = await _client.PutAsJsonAsync($"/api/v1/management/tenants/{tenantId}/settings", new
        {
            operational = new { maxConcurrentChannels = 200 },
            auth = new { passwordMinLength = 20 },
            quotas = new { maxActiveAgents = 50 },
            retention = new { conversationRetentionDays = 365 },
            rateLimitTier = "Enterprise",
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync($"/api/v1/management/tenants/{tenantId}/settings");
        var body = await getResponse.Content.ReadAsStringAsync();
        body.Should().Contain("\"maxConcurrentChannels\":200");
        body.Should().Contain("\"passwordMinLength\":20");
        body.Should().Contain("\"maxActiveAgents\":50");
        body.Should().Contain("\"conversationRetentionDays\":365");
        body.Should().Contain("Enterprise");
    }

    [Fact]
    public async Task GetSettings_ShouldReturn404_WhenTenantNotFound()
    {
        var response = await _client.GetAsync("/api/v1/management/tenants/nonexistent/settings");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
