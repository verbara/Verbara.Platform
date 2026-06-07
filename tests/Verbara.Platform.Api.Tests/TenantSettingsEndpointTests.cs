using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests;

public sealed class TenantSettingsEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public TenantSettingsEndpointTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
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

    [Fact]
    public async Task UpdateSettings_ShouldRoundTripOutboundCallerId_WhenOperationalSet()
    {
        // 3B.2d — the tenant-level outbound caller ID drives agent click-to-dial + external transfer.
        var response = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            operational = new { outboundCallerId = "+15558675309" },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync("/api/v1/admin/tenant/settings");
        var body = await getResponse.Content.ReadAsStringAsync();
        body.Should().Contain("\"outboundCallerId\":\"+15558675309\"");
    }

    // ─── W6-A7 per-tenant default channel capacity ─────────────────────────────

    [Fact]
    public async Task GetSettings_ShouldReturnCapacityDefaults_WhenRead()
    {
        var response = await _client.GetAsync("/api/v1/admin/tenant/settings");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var op = dto["operational"]!;
        // Fresh-tenant defaults from TenantAuthConfig: 1/3/5/3/5.
        op["maxVoiceDefault"]!.GetValue<int>().Should().Be(1);
        op["maxChatDefault"]!.GetValue<int>().Should().Be(3);
        op["maxEmailDefault"]!.GetValue<int>().Should().Be(5);
        op["maxSmsDefault"]!.GetValue<int>().Should().Be(3);
        op["maxTotalDefault"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public async Task UpdateSettings_ShouldPersistCapacityDefaults_WhenOperationalSet()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            operational = new
            {
                maxVoiceDefault = 1,
                maxChatDefault = 8,
                maxEmailDefault = 10,
                maxSmsDefault = 6,
                maxTotalDefault = 12,
            },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync("/api/v1/admin/tenant/settings");
        var dto = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;
        var op = dto["operational"]!;
        op["maxChatDefault"]!.GetValue<int>().Should().Be(8);
        op["maxEmailDefault"]!.GetValue<int>().Should().Be(10);
        op["maxSmsDefault"]!.GetValue<int>().Should().Be(6);
        op["maxTotalDefault"]!.GetValue<int>().Should().Be(12);
    }

    [Fact]
    public async Task UpdateSettings_ShouldRejectMaxVoiceDefaultAboveOne_WhenSet()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            operational = new { maxVoiceDefault = 2 },
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateSettings_ShouldRejectCapacityDefaultOutOfBounds_WhenSet()
    {
        var negative = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            operational = new { maxChatDefault = -1 },
        });
        negative.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var tooLarge = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            operational = new { maxTotalDefault = 51 },
        });
        tooLarge.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateSettings_ShouldEmitCapacityDefaultAudit_WhenChanged()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            operational = new { maxChatDefault = 9 },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var auditStore = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var hits = await auditStore.SearchAsync(
            new TenantId(PlatformAdminApiFactory.HostTenantId),
            new AuditQuery(Action: "tenant.capacity_default_changed", Page: 1, PageSize: 100),
            CancellationToken.None);

        hits.Items.Should().Contain(e =>
            e.Category == "operational"
            && e.ActorType == "user"
            && e.Metadata != null
            && e.Metadata["new_max_chat_default"] == "9");
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
