using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

public sealed class SetupEndpointTests : IClassFixture<PlatformApiFactory>
{
    private readonly PlatformApiFactory _factory;

    public SetupEndpointTests(PlatformApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Setup_ShouldCreateHostTenant_WhenNoneExists()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "admin@setup-test.com",
            password = "SetupTest2026!",
            displayName = "Setup Admin",
            platformName = "Test Platform",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SetupResponseDto>();
        body.Should().NotBeNull();
        body!.TenantId.Should().Be("platform");
        body.UserId.Should().NotBeNullOrEmpty();
        body.AccessToken.Should().NotBeNullOrEmpty();
        body.ManagementApiKey.Should().StartWith("mgmt_");
    }

    [Fact]
    public async Task Setup_ShouldReturn409_WhenHostTenantAlreadyExists()
    {
        // Use the PlatformAdminApiFactory which already has a host tenant
        using var factory = new PlatformAdminApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "another@test.com",
            password = "AnotherTest2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Setup_ShouldReturn400_WhenEmailMissing()
    {
        // Use a fresh factory to ensure no host tenant exists yet
        using var freshFactory = new PlatformApiFactory();
        var client = freshFactory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/setup", new
        {
            email = "",
            password = "Test2026!",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record SetupResponseDto(
        string TenantId,
        string UserId,
        string AccessToken,
        string ManagementApiKey);
}
