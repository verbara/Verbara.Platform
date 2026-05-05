using System.Net;
using System.Net.Http.Json;

namespace Verbara.Platform.Api.Tests;

public sealed class EnumSerializationTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public EnumSerializationTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task AgentResponse_ShouldSerializeStateAsString()
    {
        var userBody = JsonContent.Create(new { email = "enum-test@test.com", displayName = "Enum Test", role = "Agent" });
        var userResponse = await _client.PostAsync("/api/admin/users", userBody);
        var userJson = await userResponse.Content.ReadAsStringAsync();
        userJson.Should().MatchRegex("\"role\":\"[Aa]gent\"");
        userJson.Should().NotContain("\"role\":0");
    }

    [Fact]
    public async Task QueueResponse_ShouldNotContainNumericEnums()
    {
        var body = JsonContent.Create(new { name = "Enum Test Queue" });
        var response = await _client.PostAsync("/api/admin/queues", body);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotMatchRegex("\"state\":\\d");
    }
}
