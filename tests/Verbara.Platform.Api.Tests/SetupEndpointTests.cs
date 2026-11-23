using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Api.Tests;

public sealed class SetupEndpointTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public SetupEndpointTests(PlatformApiFactory factory) => _factory = factory;

    private static object ValidBody() => new
    {
        email = "admin@setup-test.com",
        password = "PlatformPass2026!",
        displayName = "Platform Admin",
        platformName = "Test Platform",
        customerTenantId = "acme",
        customerName = "Acme Corp",
        customerAdminEmail = "ops@acme.com",
        customerAdminPassword = "CustomerPass2026!",
        customerAdminDisplayName = "Acme Admin",
    };

    private sealed record SetupResponseDto(
        string TenantId,
        string UserId,
        string AccessToken,
        string ManagementApiKey,
        string CustomerTenantId,
        string CustomerUserId);

    [Fact]
    public async Task Setup_ShouldCreateBothTenantsAndAdmins_WhenValid()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SetupResponseDto>();
        body.Should().NotBeNull();
        body!.TenantId.Should().Be("platform");
        body.CustomerTenantId.Should().Be("acme");
        body.UserId.Should().NotBeNullOrEmpty();
        body.CustomerUserId.Should().NotBeNullOrEmpty();
        body.AccessToken.Should().NotBeNullOrEmpty();
        body.ManagementApiKey.Should().StartWith("mgmt_");

        var tenantStore = factory.Services.GetRequiredService<ITenantStore>();
        var platform = await tenantStore.GetAsync("platform", default);
        platform.Should().NotBeNull();
        platform!.Type.Should().Be(TenantType.Platform);

        var customer = await tenantStore.GetAsync("acme", default);
        customer.Should().NotBeNull();
        customer!.Type.Should().Be(TenantType.Customer);
        customer.ParentTenantId.Should().Be("platform");

        var userStore = factory.Services.GetRequiredService<IUserStore>();
        var platformAdmin = await userStore.GetByEmailAsync(new TenantId("platform"), "admin@setup-test.com", default);
        platformAdmin.Should().NotBeNull();
        var customerAdmin = await userStore.GetByEmailAsync(new TenantId("acme"), "ops@acme.com", default);
        customerAdmin.Should().NotBeNull();
    }

    [Fact]
    public async Task Setup_ShouldReturn409_WhenHostTenantAlreadyExists()
    {
        using var factory = new PlatformAdminApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Setup_ShouldSucceed_WhenRetriedOverAHostTenantLeftByAFailedRun()
    {
        // Reproduces the wedged first-run install: a fault after the host tenant is
        // written but before any user exists. The old guard checked for the tenant, so
        // every retry returned 409 "Platform already initialized." with no in-product
        // recovery. Setup must now adopt the orphaned tenant and complete.
        using var factory = new PlatformApiFactory();
        var tenantStore = factory.Services.GetRequiredService<ITenantStore>();
        await tenantStore.UpsertAsync(
            new Tenant
            {
                TenantId = "platform",
                Name = "Verbara",
                Status = TenantStatus.Active,
                Type = TenantType.Platform,
                ParentTenantId = null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            default);

        var userStore = factory.Services.GetRequiredService<IUserStore>();
        var before = await userStore.ListAsync(new TenantId("platform"), new PagedQuery(1, 1), default);
        before.Items.Should().BeEmpty("the half-written state has a tenant but no user");

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/setup", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "a leftover host tenant must not wedge the install");

        var body = await response.Content.ReadFromJsonAsync<SetupResponseDto>();
        body.Should().NotBeNull();
        body!.TenantId.Should().Be("platform");

        var platformAdmin = await userStore.GetByEmailAsync(
            new TenantId("platform"), "admin@setup-test.com", default);
        platformAdmin.Should().NotBeNull("the retry must create the platform admin the failed run never wrote");

        // The leftover tenant was adopted, not duplicated or replaced by a Customer.
        var platform = await tenantStore.GetAsync("platform", default);
        platform.Should().NotBeNull();
        platform!.Type.Should().Be(TenantType.Platform);
        platform.ParentTenantId.Should().BeNull();
    }

    [Fact]
    public async Task Setup_ShouldReturn409_WhenPlatformUserAlreadyExists()
    {
        // The positive control for the test above: once a platform USER exists, setup
        // really is done and must still refuse to run a second time.
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/setup", ValidBody());
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/api/setup", ValidBody());
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenEmailMissing()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "",
            password = "PlatformPass2026!",
            customerTenantId = "acme",
            customerName = "Acme Corp",
            customerAdminEmail = "ops@acme.com",
            customerAdminPassword = "CustomerPass2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenCustomerNameMissing()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "admin@setup-test.com",
            password = "PlatformPass2026!",
            customerTenantId = "acme",
            customerName = "",
            customerAdminEmail = "ops@acme.com",
            customerAdminPassword = "CustomerPass2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenCustomerTenantIdIsPlatform()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "admin@setup-test.com",
            password = "PlatformPass2026!",
            customerTenantId = "platform",
            customerName = "Acme Corp",
            customerAdminEmail = "ops@acme.com",
            customerAdminPassword = "CustomerPass2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenEmailsMatch()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "same@acme.com",
            password = "PlatformPass2026!",
            customerTenantId = "acme",
            customerName = "Acme Corp",
            customerAdminEmail = "same@acme.com",
            customerAdminPassword = "CustomerPass2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenPasswordBelowPolicy()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "admin@setup-test.com",
            password = "short1A",
            customerTenantId = "acme",
            customerName = "Acme Corp",
            customerAdminEmail = "ops@acme.com",
            customerAdminPassword = "CustomerPass2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── partner-channel-pilot-fixes P3 / D6 / D15: plans at birth ───────────────────

    [Fact]
    public async Task Setup_ShouldCreateHostOnEnterprise_WhenPlatformIsInitialized()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var host = await factory.Services.GetRequiredService<ITenantStore>().GetAsync("platform", default);
        host!.Metadata.Should().NotBeNull().And.ContainKey("Plan");
        host.GetPlan().Should().Be(TenantPlan.Enterprise, "setup creates the host on Enterprise (decision 9)");
    }

    [Fact]
    public async Task Setup_ShouldKeepHostMetadata_WhenRepairRePostRuns()
    {
        using var factory = new PlatformApiFactory();
        var tenantStore = factory.Services.GetRequiredService<ITenantStore>();
        await tenantStore.UpsertAsync(
            new Tenant
            {
                TenantId = "platform",
                Name = "Verbara",
                Status = TenantStatus.Active,
                Type = TenantType.Platform,
                ParentTenantId = null,
                Metadata = new Dictionary<string, string> { ["SupportRegion"] = "latam" },
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            default);

        var response = await factory.CreateClient().PostAsJsonAsync("/api/setup", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var host = await tenantStore.GetAsync("platform", default);
        host!.Metadata.Should().NotBeNull();
        host.Metadata!.Should().Contain("SupportRegion", "latam", "a repair re-POST keeps the metadata the host already holds");
        host.GetPlan().Should().Be(TenantPlan.Enterprise);
    }

    [Fact]
    public async Task Setup_ShouldCreateCustomerOnHostPlan_WhenPlatformIsInitialized()
    {
        using var factory = new PlatformApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/setup", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var customer = await factory.Services.GetRequiredService<ITenantStore>().GetAsync("acme", default);
        customer!.Metadata.Should().NotBeNull().And.ContainKey("Plan", "the setup Customer is born on the host's plan, not on the Starter fallback");
        customer.GetPlan().Should().Be(TenantPlan.Enterprise);
    }

    // ─── The tenant-id rule at first-run setup (realtime-objects-stay-in-their-tenant, design D1) ───────────

    private static object BodyWithCustomer(string customerTenantId) => new
    {
        email = "admin@setup-test.com",
        password = "PlatformPass2026!",
        displayName = "Platform Admin",
        platformName = "Test Platform",
        customerTenantId,
        customerName = "Customer",
        customerAdminEmail = "ops@customer.test",
        customerAdminPassword = "CustomerPass2026!",
    };

    private static async Task SeedOrphanAsync(IServiceProvider services, string tenantId, TenantType type = TenantType.Customer)
    {
        await services.GetRequiredService<ITenantStore>().UpsertAsync(new Tenant
        {
            TenantId = tenantId,
            Name = tenantId,
            Status = TenantStatus.Active,
            Type = type,
            ParentTenantId = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, default);
    }

    private static async Task AssertNoUserAndNoKeyAsync(IServiceProvider services, params string[] tenantIds)
    {
        var users = services.GetRequiredService<IUserStore>();
        var keys = services.GetRequiredService<IApiKeyStore>();
        foreach (var t in tenantIds)
        {
            (await users.ListAsync(new TenantId(t), new PagedQuery(1, 10), default)).Items.Should().BeEmpty($"no user of {t} is written");
            (await keys.ListAsync(new TenantId(t), new PagedQuery(1, 10), default)).Items.Should().BeEmpty($"no key of {t} is written");
        }
    }

    [Fact]
    public async Task Setup_ShouldReturn400AndCreateNoTenant_WhenTheCustomerIdIsReserved()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", BodyWithCustomer("t"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        var store = factory.Services.GetRequiredService<ITenantStore>();
        (await store.GetAsync("t", default)).Should().BeNull();
        (await store.GetAsync("platform", default)).Should().BeNull("a refused setup writes no tenant");
    }

    [Fact]
    public async Task Setup_ShouldReturn409AndWriteNothing_WhenTheHostIdExtendsTheCustomerId()
    {
        using var factory = new PlatformApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", BodyWithCustomer("platform-one"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        var store = factory.Services.GetRequiredService<ITenantStore>();
        (await store.GetAsync("platform", default)).Should().BeNull();
        (await store.GetAsync("platform-one", default)).Should().BeNull();
        await AssertNoUserAndNoKeyAsync(factory.Services, "platform", "platform-one");
    }

    [Fact]
    public async Task Setup_ShouldReturn409AndWriteNothing_WhenTheCustomerIdOverlapsAStoredTenant()
    {
        using var factory = new PlatformApiFactory();
        _ = factory.Server;
        await SeedOrphanAsync(factory.Services, "acme");
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", BodyWithCustomer("acme-corp"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        var store = factory.Services.GetRequiredService<ITenantStore>();
        (await store.GetAsync("platform", default)).Should().BeNull();
        (await store.GetAsync("acme-corp", default)).Should().BeNull();
        (await store.GetAsync("acme", default))!.ParentTenantId.Should().BeNull("nothing is adopted before the refusal");
        await AssertNoUserAndNoKeyAsync(factory.Services, "platform", "acme-corp");
    }

    [Theory]
    [InlineData("platform-x")]
    [InlineData("PLATFORM")]
    public async Task Setup_ShouldReturn409AndWriteNothing_WhenTheHostIsNotStoredAndAStoredIdOverlapsIt(string stored)
    {
        using var factory = new PlatformApiFactory();
        _ = factory.Server;
        await SeedOrphanAsync(factory.Services, stored);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", BodyWithCustomer("acme"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        var store = factory.Services.GetRequiredService<ITenantStore>();
        // The in-memory store looks ids up ignoring case, so `PLATFORM` answers a lookup of `platform`.
        (await store.GetAsync("platform", default))?.TenantId.Should().NotBe("platform", "writing the host would create a new pair");
        (await store.GetAsync("acme", default)).Should().BeNull();
        (await store.GetAsync(stored, default))!.ParentTenantId.Should().BeNull("nothing is adopted before the refusal");
        await AssertNoUserAndNoKeyAsync(factory.Services, "platform", "acme");
    }

    [Fact]
    public async Task Setup_ShouldNotRefuse_WhenTheHostIsAlreadyStoredWithAnOverlappingId()
    {
        // A pair that already includes the stored host is not created by setup; the audit reports it, and a repair
        // re-POST over a half-written install stays possible.
        using var factory = new PlatformApiFactory();
        _ = factory.Server;
        await SeedOrphanAsync(factory.Services, "platform", TenantType.Platform);
        await SeedOrphanAsync(factory.Services, "platform-x");
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", BodyWithCustomer("acme"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Setup_ShouldAdoptTheStoredTenant_WhenItsIdIsExactlyTheCustomerId()
    {
        using var factory = new PlatformApiFactory();
        _ = factory.Server;
        await SeedOrphanAsync(factory.Services, "platform", TenantType.Platform);
        await SeedOrphanAsync(factory.Services, "acme");
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", BodyWithCustomer("acme"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var store = factory.Services.GetRequiredService<ITenantStore>();
        (await store.GetAsync("acme", default))!.ParentTenantId.Should().Be("platform");
        (await factory.Services.GetRequiredService<IUserStore>().ListAsync(new TenantId("platform"), new PagedQuery(1, 1), default))
            .Items.Should().ContainSingle("the platform administrator exists");
    }
}
