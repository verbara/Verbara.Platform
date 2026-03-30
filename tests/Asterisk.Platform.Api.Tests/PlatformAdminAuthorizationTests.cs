using System.Net;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests;

public sealed class PlatformAdminAuthorizationTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly PlatformAdminApiFactory _factory;

    public PlatformAdminAuthorizationTests(PlatformAdminApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ManagementEndpoint_ShouldGrantAccess_WhenManagementApiKey()
    {
        var client = _factory.CreatePlatformAdminClient();
        var response = await client.GetAsync("/api/management/tenants");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ManagementEndpoint_ShouldDenyAccess_WhenNoAuth()
    {
        var client = _factory.CreateAnonymousClient();
        var response = await client.GetAsync("/api/management/tenants");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ManagementEndpoint_ShouldDenyAccess_WhenStandardApiKey()
    {
        // Use the authenticated factory which has a standard tenant-scoped key
        using var stdFactory = new AuthenticatedPlatformApiFactory();
        var client = stdFactory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/management/tenants");
        // Standard key is Admin but not in host tenant — should be denied
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
