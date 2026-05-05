using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests;

public sealed class DispositionEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public DispositionEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task CreateDisposition_ShouldReturn201()
    {
        var body = JsonContent.Create(new { name = "Sale Completed", category = "Success" });
        var response = await _client.PostAsync("/api/admin/dispositions", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ListDispositions_ShouldReturn200()
    {
        var response = await _client.GetAsync("/api/admin/dispositions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetDisposition_ShouldReturn404_WhenNotFound()
    {
        var response = await _client.GetAsync("/api/admin/dispositions/nonexistent-id");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateAndGetDisposition_ShouldReturn200()
    {
        var body = JsonContent.Create(new { name = "Callback Scheduled", category = "FollowUp" });
        var createResponse = await _client.PostAsync("/api/admin/dispositions", body);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var created = JsonNode.Parse(await createResponse.Content.ReadAsStringAsync());
        var id = created!["dispositionId"]!.GetValue<string>();

        var getResponse = await _client.GetAsync($"/api/admin/dispositions/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await getResponse.Content.ReadAsStringAsync();
        content.Should().Contain("Callback Scheduled");
    }

    [Fact]
    public async Task DeleteDisposition_ShouldReturn204()
    {
        var body = JsonContent.Create(new { name = "Temp", category = "Failure" });
        var createResponse = await _client.PostAsync("/api/admin/dispositions", body);
        var created = JsonNode.Parse(await createResponse.Content.ReadAsStringAsync());
        var id = created!["dispositionId"]!.GetValue<string>();

        var response = await _client.DeleteAsync($"/api/admin/dispositions/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
