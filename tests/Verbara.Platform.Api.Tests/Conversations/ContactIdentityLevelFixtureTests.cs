using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// The computed <c>identityLevel</c> member of a contact, against the four contacts of
/// <c>Conversations/Fixtures/contact-identity-level.v1.json</c>.
/// </summary>
public sealed class ContactIdentityLevelFixtureTests : IClassFixture<AuthenticatedPlatformApiFactory>
{
    private readonly HttpClient _client;

    public ContactIdentityLevelFixtureTests(AuthenticatedPlatformApiFactory factory) =>
        _client = factory.CreateAuthenticatedClient();

    private static JsonObject Fixture()
    {
        var file = Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", "contact-identity-level.v1.json");
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    public static TheoryData<int> ExampleIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Fixture()["examples"]!.AsArray().Count; i++)
            data.Add(i);
        return data;
    }

    private static Contact ContactOf(JsonNode body)
    {
        var contact = new Contact
        {
            ContactId = EntityId.From(body["contactId"]!.GetValue<string>()),
            TenantId = new TenantId(body["tenantId"]!.GetValue<string>()),
            CreatedAt = body["createdAt"]!.GetValue<DateTimeOffset>(),
        };
        foreach (var address in body["addresses"]!.AsArray())
        {
            contact.AddAddress(new ChannelAddress(
                Enum.Parse<ChannelType>(address!["channel"]!.GetValue<string>()),
                address["address"]!.GetValue<string>()));
        }

        return contact;
    }

    [Theory]
    [MemberData(nameof(ExampleIndexes))]
    public void Contact_ShouldSerializeFixtureIdentityLevel_WhenAddressesAreTheFixtures(int index)
    {
        var example = Fixture()["examples"]!.AsArray()[index]!;
        var body = example["body"]!;

        var json = JsonSerializer.Serialize(ContactOf(body), ApiJsonContext.Default.Contact);
        var written = JsonNode.Parse(json)!.AsObject();

        if (body["identityLevel"] is { } expected)
        {
            written["identityLevel"]!.GetValue<string>().Should().Be(expected.GetValue<string>());
        }
        else
        {
            example["_omittedWhileNull"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Contain("identityLevel");
            written.ContainsKey("identityLevel").Should().BeFalse("a null member is absent, never written as null");
        }
    }

    [Fact]
    public async Task GetContact_ShouldCarryIdentityLevel_WhenContactHoldsWebChatAddress()
    {
        var id = await PostContactAsync("WebChat", $"webchat-{Guid.NewGuid():N}");

        var body = JsonNode.Parse(await _client.GetStringAsync($"/api/v1/contacts/{id}"))!;

        body["identityLevel"]!.GetValue<string>().Should().Be("Anonymous");
    }

    [Fact]
    public async Task GetContact_ShouldOmitIdentityLevel_WhenContactHoldsNoWebChatAddress()
    {
        var id = await PostContactAsync("WhatsApp", $"52155{Random.Shared.Next(10_000_000, 99_999_999)}");

        var body = JsonNode.Parse(await _client.GetStringAsync($"/api/v1/contacts/{id}"))!.AsObject();

        body.ContainsKey("identityLevel").Should().BeFalse();
    }

    private async Task<string> PostContactAsync(string channel, string address)
    {
        var request = new JsonObject
        {
            ["firstName"] = "Ana",
            ["addresses"] = new JsonArray(new JsonObject { ["channel"] = channel, ["address"] = address }),
        };
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(new Uri("/api/v1/contacts", UriKind.Relative), content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        return JsonNode.Parse(text)!["contactId"]!.GetValue<string>();
    }
}
