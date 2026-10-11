using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Verbara.Platform.Api.Tests.PartnerChannel;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests;

public sealed class ManagementTenantEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;
    private readonly HttpClient _client;

    public ManagementTenantEndpointTests(PlatformAdminApiFactory factory)
    {
        _factory = factory;
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

    [Fact]
    public async Task SuspendTenant_ShouldPersistSuspendedStatus()
    {
        var tenantId = "suspend-lifecycle-" + Guid.NewGuid().ToString("N")[..8];
        await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId,
            name = "Lifecycle Test",
            type = 2,
        });
        await _client.PostAsync($"/api/management/tenants/{tenantId}/suspend", null);

        var getResponse = await _client.GetAsync($"/api/management/tenants/{tenantId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await getResponse.Content.ReadAsStringAsync();
        body.Should().Contain("Suspended");
    }

    // ─── partner-channel-pilot-fixes D15 / P4 (partner-channel-tenancy) ──────────────

    // A fresh host on Enterprise, so "inherits the host's plan" is told apart from the Starter fallback.
    private static async Task<PlatformAdminApiFactory> EnterpriseHostAsync()
    {
        var factory = new PlatformAdminApiFactory();
        _ = factory.Server;
        await PartnerChannelTestKit.SeedTenantAsync(factory.Services, PlatformAdminApiFactory.HostTenantId,
            TenantType.Platform, null, TenantPlan.Enterprise);
        return factory;
    }

    private static async Task<string?> CreatedPlanAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, raw);
        return JsonNode.Parse(raw)!["metadata"]?["Plan"]?.GetValue<string>();
    }

    [Fact]
    public async Task CreateTenant_ShouldInheritHostPlan_WhenCustomerHasNoMetadataPlan()
    {
        await using var factory = await EnterpriseHostAsync();

        var response = await factory.CreatePlatformAdminClient().PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "dc1",
            name = "Direct Customer",
            type = 2,
        });

        (await CreatedPlanAsync(response)).Should().Be("Enterprise");
        (await PartnerChannelTestKit.GetTenantAsync(factory.Services, "dc1"))!.GetPlan().Should().Be(TenantPlan.Enterprise);
    }

    [Fact]
    public async Task CreateTenant_ShouldInheritHostPlan_WhenPartnerHasNoMetadataPlan()
    {
        await using var factory = await EnterpriseHostAsync();

        var response = await factory.CreatePlatformAdminClient().PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "p1",
            name = "Partner One",
            type = 1,
        });

        (await CreatedPlanAsync(response)).Should().Be("Enterprise");
    }

    [Fact]
    public async Task CreateTenant_ShouldInheritPartnerPlan_WhenCustomerUnderPartnerHasNoMetadataPlan()
    {
        await using var factory = await EnterpriseHostAsync();
        await PartnerChannelTestKit.SeedTenantAsync(factory.Services, "p1", TenantType.Partner,
            PlatformAdminApiFactory.HostTenantId, TenantPlan.Pro);

        var response = await factory.CreatePlatformAdminClient().PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "pc1",
            name = "Partner Customer",
            type = 2,
            parentTenantId = "p1",
        });

        (await CreatedPlanAsync(response)).Should().Be("Pro");
    }

    [Fact]
    public async Task CreateTenant_ShouldKeepExplicitPlan_WhenMetadataPlanBelowParent()
    {
        await using var factory = await EnterpriseHostAsync();

        var response = await factory.CreatePlatformAdminClient().PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "dc-starter",
            name = "Starter Customer",
            type = 2,
            metadata = new Dictionary<string, string> { ["Plan"] = "Starter" },
        });

        (await CreatedPlanAsync(response)).Should().Be("Starter");
    }

    [Fact]
    public async Task CreateTenant_ShouldReturn400_WhenMetadataPlanUnknown()
    {
        var tenantId = "gold-" + Guid.NewGuid().ToString("N")[..8];

        var response = await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId,
            name = "Gold Customer",
            type = 2,
            metadata = new Dictionary<string, string> { ["Plan"] = "Gold" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartnerChannelTestKit.GetTenantAsync(_factory.Services, tenantId)).Should().BeNull("no tenant is created");
    }

    [Fact]
    public async Task CreateTenant_ShouldReturn409PlanExceedsParent_WhenMetadataPlanAboveParent()
    {
        await using var factory = await EnterpriseHostAsync();
        await PartnerChannelTestKit.SeedTenantAsync(factory.Services, "p1", TenantType.Partner,
            PlatformAdminApiFactory.HostTenantId, TenantPlan.Pro);

        var response = await factory.CreatePlatformAdminClient().PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "pc-above",
            name = "Above Partner",
            type = 2,
            parentTenantId = "p1",
            metadata = new Dictionary<string, string> { ["Plan"] = "Enterprise" },
        });

        var body = await PartnerChannelTestKit.AssertRefusalAsync(response, HttpStatusCode.Conflict,
            PartnerChannelTestKit.FixtureBody("plan-above-parent-409.json"), "plan-exceeds-parent");
        body["requestedPlan"]!.GetValue<string>().Should().Be("Enterprise");
        body["parentPlan"]!.GetValue<string>().Should().Be("Pro");
        body["parentTenantId"]!.GetValue<string>().Should().Be("p1");
        (await PartnerChannelTestKit.GetTenantAsync(factory.Services, "pc-above")).Should().BeNull("no tenant is created");
    }

    [Fact]
    public async Task UpdateTenant_ShouldReturn409PlanExceedsParent_WhenMetadataPlanAboveParent()
    {
        await using var factory = await EnterpriseHostAsync();
        await PartnerChannelTestKit.SeedTenantAsync(factory.Services, "p1", TenantType.Partner,
            PlatformAdminApiFactory.HostTenantId, TenantPlan.Pro);
        await PartnerChannelTestKit.SeedTenantAsync(factory.Services, "pc1", TenantType.Customer, "p1", TenantPlan.Pro);

        var response = await factory.CreatePlatformAdminClient().PutAsJsonAsync("/api/management/tenants/pc1", new
        {
            name = "Renamed",
            metadata = new Dictionary<string, string> { ["Plan"] = "Enterprise" },
        });

        var body = await PartnerChannelTestKit.AssertRefusalAsync(response, HttpStatusCode.Conflict,
            PartnerChannelTestKit.FixtureBody("plan-above-parent-409.json"), "plan-exceeds-parent");
        body["requestedPlan"]!.GetValue<string>().Should().Be("Enterprise");
        body["parentPlan"]!.GetValue<string>().Should().Be("Pro");
        body["parentTenantId"]!.GetValue<string>().Should().Be("p1");
        var stored = await PartnerChannelTestKit.GetTenantAsync(factory.Services, "pc1");
        stored!.GetPlan().Should().Be(TenantPlan.Pro, "the refused plan is not stored");
        stored!.Name.Should().Be("pc1", "nothing of the refused request is written");
    }

    [Fact]
    public async Task UpdateTenant_ShouldReturn400_WhenMetadataPlanUnknown()
    {
        await using var factory = await EnterpriseHostAsync();
        await PartnerChannelTestKit.SeedTenantAsync(factory.Services, "p1", TenantType.Partner,
            PlatformAdminApiFactory.HostTenantId, TenantPlan.Pro);
        await PartnerChannelTestKit.SeedTenantAsync(factory.Services, "pc1", TenantType.Customer, "p1", TenantPlan.Pro);

        var response = await factory.CreatePlatformAdminClient().PutAsJsonAsync("/api/management/tenants/pc1", new
        {
            metadata = new Dictionary<string, string> { ["Plan"] = "Gold" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await PartnerChannelTestKit.GetTenantAsync(factory.Services, "pc1"))!.Metadata!["Plan"].Should().Be("Pro");
    }

    [Fact]
    public async Task UpdateTenant_ShouldStorePlan_WhenMetadataPlanAtOrBelowParent()
    {
        await using var factory = await EnterpriseHostAsync();
        await PartnerChannelTestKit.SeedTenantAsync(factory.Services, "p1", TenantType.Partner,
            PlatformAdminApiFactory.HostTenantId, TenantPlan.Pro);
        await PartnerChannelTestKit.SeedTenantAsync(factory.Services, "pc1", TenantType.Customer, "p1", TenantPlan.Starter);

        var response = await factory.CreatePlatformAdminClient().PutAsJsonAsync("/api/management/tenants/pc1", new
        {
            metadata = new Dictionary<string, string> { ["Plan"] = "Pro" },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await PartnerChannelTestKit.GetTenantAsync(factory.Services, "pc1"))!.GetPlan().Should().Be(TenantPlan.Pro);
    }

    [Fact]
    public async Task CreateTenant_ShouldReturn400_WhenPartnerParentIsPartner()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parent = "pp-" + suffix;
        _ = _factory.Server;
        await PartnerChannelTestKit.SeedTenantAsync(_factory.Services, parent, TenantType.Partner,
            PlatformAdminApiFactory.HostTenantId, TenantPlan.Pro);

        var response = await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "pchild-" + suffix,
            name = "Nested Partner",
            type = 1,
            parentTenantId = parent,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartnerChannelTestKit.GetTenantAsync(_factory.Services, "pchild-" + suffix)).Should().BeNull();
    }

    [Fact]
    public async Task CreateTenant_ShouldReturn400_WhenPartnerParentIsCustomer()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parent = "dc-" + suffix;
        _ = _factory.Server;
        await PartnerChannelTestKit.SeedTenantAsync(_factory.Services, parent, TenantType.Customer,
            PlatformAdminApiFactory.HostTenantId, TenantPlan.Pro);

        var response = await _client.PostAsJsonAsync("/api/management/tenants", new
        {
            tenantId = "pchild-" + suffix,
            name = "Partner Under Customer",
            type = 1,
            parentTenantId = parent,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PartnerChannelTestKit.GetTenantAsync(_factory.Services, "pchild-" + suffix)).Should().BeNull();
    }
}
