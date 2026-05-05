using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Verbara.Platform.Api.Tests;

public sealed class ContactEndpointTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public ContactEndpointTests(AuthenticatedPlatformApiFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task CreateContact_ShouldReturn201_WithNewContact()
    {
        var body = JsonContent.Create(new
        {
            firstName = "Jane",
            lastName = "Doe",
            addresses = new[] { new { channel = "WhatsApp", address = "+5491112345678" } },
        });

        var response = await _client.PostAsync("/api/contacts", body);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("Jane");
        json.Should().Contain("Doe");
    }

    [Fact]
    public async Task UpdateContact_ShouldReturn200_WhenContactExists()
    {
        var createBody = JsonContent.Create(new { firstName = "Update", lastName = "Test" });
        var createResponse = await _client.PostAsync("/api/contacts", createBody);
        var created = JsonNode.Parse(await createResponse.Content.ReadAsStringAsync());
        var contactId = created!["contactId"]!.GetValue<string>();

        var updateBody = JsonContent.Create(new { firstName = "Updated", company = "ACME" });
        var response = await _client.PutAsync($"/api/contacts/{contactId}", updateBody);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("Updated");
        json.Should().Contain("ACME");
    }

    [Fact]
    public async Task DeleteContact_ShouldReturn204_WhenContactExists()
    {
        var createBody = JsonContent.Create(new { firstName = "Delete", lastName = "Me" });
        var createResponse = await _client.PostAsync("/api/contacts", createBody);
        var created = JsonNode.Parse(await createResponse.Content.ReadAsStringAsync());
        var contactId = created!["contactId"]!.GetValue<string>();

        var response = await _client.DeleteAsync($"/api/contacts/{contactId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
