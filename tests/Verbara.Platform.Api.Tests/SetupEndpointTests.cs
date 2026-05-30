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
}
