using System.Net;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

public sealed class ManagementClusterEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public ManagementClusterEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task ClusterStatus_ShouldReturnLocalFallback()
    {
        var response = await _client.GetAsync("/api/management/cluster/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("instanceId");
    }

    [Fact]
    public async Task ClusterNodes_ShouldReturnEmptyWhenNoCluster()
    {
        var response = await _client.GetAsync("/api/management/cluster/nodes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
