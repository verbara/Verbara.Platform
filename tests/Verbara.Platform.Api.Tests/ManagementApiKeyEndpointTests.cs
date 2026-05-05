using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests;

public sealed class ManagementApiKeyEndpointTests : IClassFixture<PlatformAdminApiFactory>
{
    private readonly HttpClient _client;

    public ManagementApiKeyEndpointTests(PlatformAdminApiFactory factory)
    {
        _client = factory.CreatePlatformAdminClient();
    }

    [Fact]
    public async Task ListKeys_ShouldReturnSeededKey()
    {
        var response = await _client.GetAsync("/api/management/api-keys");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Test Management Key");
    }

    [Fact]
    public async Task CreateKey_ShouldReturnNewKey()
    {
        var response = await _client.PostAsJsonAsync("/api/management/api-keys", new
        {
            name = "CI/CD Key",
            expiresInDays = 30,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("mgmt_");
        body.Should().Contain("CI/CD Key");
    }

    [Fact]
    public async Task RevokeKey_ShouldReturnNoContent()
    {
        // Create a key to revoke
        var createResponse = await _client.PostAsJsonAsync("/api/management/api-keys", new
        {
            name = "Revoke Test",
        });
        var created = await createResponse.Content.ReadFromJsonAsync<KeyResponseDto>();

        var response = await _client.DeleteAsync($"/api/management/api-keys/{created!.KeyId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private sealed record KeyResponseDto(string KeyId, string Name, string ApiKey, DateTimeOffset? ExpiresAt);
}
