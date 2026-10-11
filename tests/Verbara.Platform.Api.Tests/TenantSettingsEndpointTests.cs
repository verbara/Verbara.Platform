using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Verbara.Platform.Api.Tests.PartnerChannel;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;
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

    [Fact]
    public async Task UpdateSettings_ShouldPersistBothAuthAndCapacityDefaults_WhenUpdatedTogether()
    {
        // W6-M3 — a SINGLE request mutating BOTH the Auth section AND the capacity defaults must
        // persist both. A7 deliberately orders the capacity-defaults block AFTER the Auth block and
        // RE-READS the cache-evicted auth config (capacity lives on TenantAuthConfig too), so the
        // capacity write does not clobber the just-saved Auth change. Lock that interleaving here.
        var response = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            auth = new { passwordMinLength = 18 },
            operational = new
            {
                maxVoiceDefault = 1,
                maxChatDefault = 7,
                maxTotalDefault = 11,
            },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var getResponse = await _client.GetAsync("/api/v1/admin/tenant/settings");
        var dto = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())!;

        // Auth change survived the subsequent capacity write.
        dto["auth"]!["passwordMinLength"]!.GetValue<int>().Should().Be(18);

        // Capacity defaults survived alongside the Auth change.
        var op = dto["operational"]!;
        op["maxChatDefault"]!.GetValue<int>().Should().Be(7);
        op["maxTotalDefault"]!.GetValue<int>().Should().Be(11);
    }

    // ─── partner-channel-pilot-fixes P6 (partner-channel-tenancy, design D7) ─────────

    [Fact]
    public async Task UpdateSettings_ShouldIgnoreNodeAffinity_WhenCallerIsTenantAdmin()
    {
        var host = await _client.PutAsJsonAsync(
            $"/api/v1/management/tenants/{PlatformAdminApiFactory.HostTenantId}/settings",
            new { operational = new { nodeAffinity = PartnerChannelTestKit.NodeA } });
        host.StatusCode.Should().Be(HttpStatusCode.OK, "the host places the tenant first");

        var response = await _client.PutAsJsonAsync("/api/v1/admin/tenant/settings", new
        {
            operational = new { nodeAffinity = PartnerChannelTestKit.NodeB, maxActiveCampaigns = 11 },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tenant = await PartnerChannelTestKit.GetTenantAsync(_factory.Services, PlatformAdminApiFactory.HostTenantId);
        tenant!.Options.NodeAffinity.Should().Equal(["node-a"], "a tenant's own administrator cannot overwrite its placement");
        tenant.Options.MaxActiveCampaigns.Should().Be(11);
    }
}

public sealed class ManagementTenantSettingsEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public ManagementTenantSettingsEndpointTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
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

    // ─── partner-channel-pilot-fixes P3 / P6 (partner-channel-tenancy) ───────────────

    [Fact]
    public async Task UpdateManagementSettings_ShouldReturn409PlanExceedsParent_WhenPlanAboveParent()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var partnerId = "ceiling-p-" + suffix;
        var customerId = "ceiling-c-" + suffix;
        _ = _factory.Server;
        await PartnerChannelTestKit.SeedTenantAsync(_factory.Services, partnerId, TenantType.Partner,
            PlatformAdminApiFactory.HostTenantId, TenantPlan.Pro);
        await PartnerChannelTestKit.SeedTenantAsync(_factory.Services, customerId, TenantType.Customer, partnerId, TenantPlan.Starter);

        var response = await _client.PutAsJsonAsync($"/api/v1/management/tenants/{customerId}/settings", new
        {
            plan = "Enterprise",
            operational = new { maxConcurrentChannels = 3 },
        });

        var body = await PartnerChannelTestKit.AssertRefusalAsync(response, HttpStatusCode.Conflict,
            PartnerChannelTestKit.FixtureBody("plan-above-parent-409.json"), "plan-exceeds-parent");
        body["requestedPlan"]!.GetValue<string>().Should().Be("Enterprise");
        body["parentPlan"]!.GetValue<string>().Should().Be("Pro");
        body["parentTenantId"]!.GetValue<string>().Should().Be(partnerId);
        var stored = await PartnerChannelTestKit.GetTenantAsync(_factory.Services, customerId);
        stored!.Options.MaxConcurrentChannels.Should().Be(100, "nothing in the refused request is saved");
        stored.GetPlan().Should().Be(TenantPlan.Starter);
    }

    [Fact]
    public async Task UpdateManagementSettings_ShouldSaveEnterprise_WhenHostWasSetUpByThisVersion()
    {
        using var factory = new PlatformApiFactory();
        var anonymous = factory.CreateClient();
        var setup = await anonymous.PostAsJsonAsync("/api/setup", new
        {
            email = "admin@ceiling-test.com",
            password = "PlatformPass2026!",
            customerTenantId = "dc1",
            customerName = "Direct Customer",
            customerAdminEmail = "ops@dc1.com",
            customerAdminPassword = "CustomerPass2026!",
        });
        setup.StatusCode.Should().Be(HttpStatusCode.Created);
        var key = JsonNode.Parse(await setup.Content.ReadAsStringAsync())!["managementApiKey"]!.GetValue<string>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");

        var response = await client.PutAsJsonAsync("/api/v1/management/tenants/dc1/settings", new { plan = "Enterprise" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var dc1 = await PartnerChannelTestKit.GetTenantAsync(factory.Services, "dc1");
        dc1!.GetPlan().Should().Be(TenantPlan.Enterprise, "a host born on Enterprise lets a direct Customer reach Enterprise");
    }

    [Fact]
    public async Task UpdateManagementSettings_ShouldWriteNodeAffinity_WhenCallerIsPlatformAdmin()
    {
        var tenantId = "affinity-host-" + Guid.NewGuid().ToString("N")[..8];
        _ = _factory.Server;
        await PartnerChannelTestKit.SeedTenantAsync(_factory.Services, tenantId, TenantType.Customer,
            PlatformAdminApiFactory.HostTenantId, plan: null);

        var response = await _client.PutAsJsonAsync($"/api/v1/management/tenants/{tenantId}/settings", new
        {
            operational = new { nodeAffinity = PartnerChannelTestKit.NodeA },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await PartnerChannelTestKit.GetTenantAsync(_factory.Services, tenantId);
        stored!.Options.NodeAffinity.Should().Equal(["node-a"], "the host still places tenants");
    }
}
