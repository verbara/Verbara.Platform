using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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

    // ─── The tenant-id rule (realtime-objects-stay-in-their-tenant, design D1) ───────────────────────────────

    private static string Unique(string stem) => stem + Guid.NewGuid().ToString("N")[..8];

    private async Task SeedCustomerAsync(string tenantId, string name = "Seeded", TenantStatus status = TenantStatus.Active)
    {
        _ = _factory.Server;
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        await store.UpsertAsync(Named(tenantId, name));
        if (status != TenantStatus.Active)
            await store.UpdateStatusAsync(tenantId, status);
    }

    [Fact]
    public async Task CreateTenant_ShouldReturn400AndCreateNothing_WhenTheIdIsNotASlug()
    {
        var response = await _client.PostAsJsonAsync("/api/management/tenants", new { tenantId = "a c", name = "Spaced", type = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await PartnerChannelTestKit.GetTenantAsync(_factory.Services, "a c")).Should().BeNull();
    }

    [Fact]
    public async Task CreateTenant_ShouldReturn409_WhenAStoredTenantExtendsTheId()
    {
        var requested = Unique("acme");
        await SeedCustomerAsync(requested + "-corp");

        var response = await _client.PostAsJsonAsync("/api/management/tenants", new { tenantId = requested, name = "Acme", type = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await PartnerChannelTestKit.GetTenantAsync(_factory.Services, requested)).Should().BeNull();
    }

    [Fact]
    public async Task CreateTenant_ShouldReturn409_WhenTheIdOfADeletedTenantIsRequestedAgain()
    {
        var stored = Unique("gone");
        await SeedCustomerAsync(stored, status: TenantStatus.Deleted);

        var response = await _client.PostAsJsonAsync("/api/management/tenants", new { tenantId = stored, name = "Again", type = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await PartnerChannelTestKit.GetTenantAsync(_factory.Services, stored))!.Status.Should().Be(TenantStatus.Deleted);
    }

    [Fact]
    public async Task CreateTenant_ShouldReturn409AndRunNoLifecycleHandler_WhenTheTenantAlreadyExists()
    {
        var recorder = new RecordingLifecycleHandler();
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(
            s => s.AddSingleton<ITenantLifecycleHandler>(recorder)));
        using var client = PlatformAdminApiFactory.CreatePlatformAdminClient(factory);
        var id = Unique("globex");
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ITenantStore>().UpsertAsync(Named(id, "Globex"));

        var response = await client.PostAsJsonAsync("/api/management/tenants", new { tenantId = id, name = "Other", type = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await PartnerChannelTestKit.GetTenantAsync(factory.Services, id))!.Name.Should().Be("Globex");
        recorder.Created.Should().BeEmpty("a refused create runs no lifecycle handler");
    }

    private static Tenant Named(string tenantId, string name) => new()
    {
        TenantId = tenantId,
        Name = name,
        Status = TenantStatus.Active,
        Type = TenantType.Customer,
        ParentTenantId = PlatformAdminApiFactory.HostTenantId,
        Metadata = new Dictionary<string, string> { ["Plan"] = TenantPlan.Pro.ToString() },
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class RecordingLifecycleHandler : ITenantLifecycleHandler
    {
        public List<string> Created { get; } = [];

        public ValueTask OnTenantCreatedAsync(Tenant tenant, CancellationToken cancellationToken = default)
        {
            lock (Created) Created.Add(tenant.TenantId);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnTenantSuspendedAsync(string tenantId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask OnTenantDeletedAsync(string tenantId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
