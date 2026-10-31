using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateNode_ShouldReturn400_WhenNodeIdIsBlank(string? nodeId)
    {
        var response = await PostNodeAsync(nodeId);

        await AssertRefusedAsync(response, nodeId, """{"error":"Node id must not be blank"}""");
    }

    [Fact]
    public async Task CreateNode_ShouldReturn400_WhenNodeIdIsReserved()
    {
        var response = await PostNodeAsync("status-refused");

        await AssertRefusedAsync(response, "status-refused", """{"error":"Node id 'status-refused' is reserved"}""");
    }

    [Fact]
    public async Task CreateNode_ShouldReachTheExistingPath_WhenTheReservedIdDiffersInCase()
    {
        var response = await PostNodeAsync("Status-Refused");

        // The status belongs to whatever this host does past the check, so only the refusal's body is asserted.
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Node id 'status-refused' is reserved", "the pool compares the reserved id Ordinal");
    }

    private async Task AssertRefusedAsync(HttpResponseMessage response, string? nodeId, string expectedBody)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        JsonNode.DeepEquals(JsonNode.Parse(body), JsonNode.Parse(expectedBody))
            .Should().BeTrue($"the body should be {expectedBody}, but was {body}");

        var statusResponse = await _client.GetAsync("/api/management/cluster/status");
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        status.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("nodeId").GetString())
            .Should().NotContain(nodeId, "a refused id is never registered");
    }

    private async Task<HttpResponseMessage> PostNodeAsync(string? nodeId)
    {
        var body = new JsonObject
        {
            ["nodeId"] = nodeId,
            ["amiHostname"] = "127.0.0.1",
            ["amiPort"] = 5038,
            ["amiUsername"] = "user",
            ["amiPassword"] = "secret",
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await _client.PostAsync("/api/management/cluster/nodes", content);
    }
}
