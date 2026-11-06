using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Contacts;

/// <summary>
/// contacts-writes-follow-their-permissions, task 1.1: regression tests written first, against the unfixed code. Each
/// contact write requires the contact permission of its action (design D1, D2; fixtures/contact-write-refusals.v1.json),
/// and <c>channelConsent</c> sent on <c>PUT</c> is stored and read back (design D8;
/// fixtures/contact-channel-consent.v1.json). Every request's method, path, body, status and answer is the fixture's;
/// a fixture path's contact id is replaced by a contact seeded for the test, except the unknown id.
/// </summary>
public sealed class ContactWritePermissionTests : IClassFixture<ContactWritesApiFactory>
{
    private const string FixtureContactId = "7a1c0e2b5d3f4a6b8c9d0e1f2a3b4c5d";

    private static readonly JsonObject Refusals = ContactWritesApiFactory.Fixture("contact-write-refusals.v1.json");
    private static readonly JsonObject Consent = ContactWritesApiFactory.Fixture("contact-channel-consent.v1.json");

    private readonly ContactWritesApiFactory _factory;

    public ContactWritePermissionTests(ContactWritesApiFactory factory) => _factory = factory;

    private static JsonObject Refusal(int index) => Refusals["refusals"]![index]!.AsObject();

    private Contact SeedNamedContact()
    {
        var contact = _factory.SeedContact(new ChannelAddress(ChannelType.Voice, $"+1555{Random.Shared.Next(1000000, 9999999)}"));
        contact.FirstName = "Ana";
        contact.LastName = "Ruiz";
        _factory.SaveContact(contact);
        return contact;
    }

    private static string Snapshot(Contact? contact) =>
        contact is null
            ? "<deleted>"
            : $"{contact.FirstName}|{contact.LastName}|{contact.DoNotContact}|{contact.UpdatedAt:O}|" +
              string.Join(',', contact.Addresses.Select(a => $"{a.Channel}:{a.Address}"));

    private static HttpRequestMessage RequestOf(JsonObject request, string? contactId)
    {
        var path = request["path"]!.GetValue<string>();
        if (contactId is not null)
            path = path.Replace(FixtureContactId, contactId, StringComparison.Ordinal);

        var message = new HttpRequestMessage(new HttpMethod(request["method"]!.GetValue<string>()), path);
        if (request["body"] is JsonNode body)
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return message;
    }

    private static async Task ShouldMatchAsync(HttpResponseMessage response, JsonObject expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().Be(expected["status"]!.GetValue<int>(), because: $"the response was {body}");
        if (expected["body"] is JsonNode expectedBody)
        {
            JsonNode.DeepEquals(JsonNode.Parse(body), expectedBody)
                .Should().BeTrue(because: $"the body is {expectedBody.ToJsonString()}, and it was {body}");
        }
    }

    private HttpClient AgentTemplateClient() => _factory.ClientFor(_factory.NewUser(UserRole.Agent, "contact-agent"));

    [Fact]
    public async Task CreateContact_ShouldRefuseNotPermitted_WhenTheCallerHoldsOnlyTheAgentTemplate()
    {
        var refusal = Refusal(0);
        using var agent = AgentTemplateClient();

        var response = await agent.SendAsync(RequestOf(refusal["request"]!.AsObject(), contactId: null));

        await ShouldMatchAsync(response, refusal);
    }

    [Fact]
    public async Task UpdateContact_ShouldRefuseNotPermittedAndLeaveTheContact_WhenTheCallerHoldsOnlyTheAgentTemplate()
    {
        var refusal = Refusal(1);
        var contact = SeedNamedContact();
        var before = Snapshot(_factory.LoadContact(contact.ContactId));
        using var agent = AgentTemplateClient();

        var response = await agent.SendAsync(RequestOf(refusal["request"]!.AsObject(), contact.ContactId.Value));

        await ShouldMatchAsync(response, refusal);
        Snapshot(_factory.LoadContact(contact.ContactId)).Should().Be(before);
    }

    [Fact]
    public async Task UpdateContact_ShouldRefuseNotPermittedBeforeTheLookup_WhenTheIdIsUnknown()
    {
        var refusal = Refusal(2);
        using var agent = AgentTemplateClient();

        var response = await agent.SendAsync(RequestOf(refusal["request"]!.AsObject(), contactId: null));

        await ShouldMatchAsync(response, refusal);
    }

    [Fact]
    public async Task DeleteContact_ShouldRefuseNotPermittedAndKeepTheContact_WhenTheCallerHoldsOnlyTheAgentTemplate()
    {
        var refusal = Refusal(3);
        var contact = SeedNamedContact();
        var before = Snapshot(_factory.LoadContact(contact.ContactId));
        using var agent = AgentTemplateClient();

        var response = await agent.SendAsync(RequestOf(refusal["request"]!.AsObject(), contact.ContactId.Value));

        await ShouldMatchAsync(response, refusal);
        Snapshot(_factory.LoadContact(contact.ContactId)).Should().Be(before);
    }

    [Fact]
    public async Task ContactWrites_ShouldRefuseNotPermitted_WhenTheCallerIsATenantApiKeyBoundToNoUser()
    {
        // refusals[4]: the three writes, through the real API-key authentication with a valid tenant key bound to no
        // user, sent as `Authorization: Bearer <key>` (its `_auth`). The bodies are the agent cases' bodies.
        var refusal = Refusal(4);
        var contact = SeedNamedContact();
        var before = Snapshot(_factory.LoadContact(contact.ContactId));
        using var unbound = _factory.UnboundApiKeyClient();

        foreach (var request in new[] { Refusal(0), Refusal(1), refusal }.Select(r => r["request"]!.AsObject()))
        {
            var response = await unbound.SendAsync(RequestOf(request, contact.ContactId.Value));
            await ShouldMatchAsync(response, refusal);
        }

        Snapshot(_factory.LoadContact(contact.ContactId)).Should().Be(before);
    }

    // ─── review round: the permission is decided before the body is read ─────

    /// <summary>
    /// Bodies the write cannot read: an unknown channel name in an enum member, a value of the wrong type, and malformed
    /// JSON. Before the review round these never reached the handler's permission check.
    /// </summary>
    public static TheoryData<string, string> UnreadableBodies() => new()
    {
        { "POST", """{"firstName":"Ana","addresses":[{"channel":"Fax","address":"+15551234567"}]}""" },
        { "POST", """{"preferredChannel":"Fax"}""" },
        { "POST", """{"firstName":""" },
        { "PUT", """{"preferredChannel":"Fax"}""" },
        { "PUT", """{"doNotContact":"yes"}""" },
        { "PUT", """{"channelConsent":{"Voice":"yes"}}""" },
        { "PUT", """{"doNotContact":""" },
    };

    [Theory]
    [MemberData(nameof(UnreadableBodies))]
    public async Task ContactWrites_ShouldRefuseNotPermitted_WhenTheCallerLacksThePermissionAndTheBodyCannotBeRead(string method, string json)
    {
        var expected = method == "POST" ? Refusal(0) : Refusal(1);
        var contact = SeedNamedContact();
        var before = Snapshot(_factory.LoadContact(contact.ContactId));
        using var agent = AgentTemplateClient();
        var path = method == "POST" ? "/api/v1/contacts" : $"/api/v1/contacts/{contact.ContactId.Value}";

        var response = await agent.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        await ShouldMatchAsync(response, expected);
        Snapshot(_factory.LoadContact(contact.ContactId)).Should().Be(before);
    }

    [Theory]
    [MemberData(nameof(UnreadableBodies))]
    public async Task ContactWrites_ShouldAnswer400AndWriteNothing_WhenAPermittedCallersBodyCannotBeRead(string method, string json)
    {
        var contact = SeedNamedContact();
        var before = Snapshot(_factory.LoadContact(contact.ContactId));
        using var admin = _factory.ClientFor(_factory.NewUser(UserRole.Admin, "unreadable-admin"));
        var path = method == "POST" ? "/api/v1/contacts" : $"/api/v1/contacts/{contact.ContactId.Value}";

        var response = await admin.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

        ((int)response.StatusCode).Should().Be(400, await response.Content.ReadAsStringAsync());
        Snapshot(_factory.LoadContact(contact.ContactId)).Should().Be(before);
    }

    [Fact]
    public async Task UpdateContact_ShouldStoreChannelConsentAndReadItBack_WhenTheCallerMayEdit()
    {
        var update = Consent["update"]!.AsObject();
        var read = Consent["read"]!.AsObject();
        var contact = SeedNamedContact();
        using var admin = _factory.ClientFor(_factory.NewUser(UserRole.Admin, "contact-admin"));

        var put = await admin.SendAsync(RequestOf(update["request"]!.AsObject(), contact.ContactId.Value));
        var putBody = await put.Content.ReadAsStringAsync();
        ((int)put.StatusCode).Should().Be(update["response"]!["status"]!.GetValue<int>(), because: $"the response was {putBody}");
        var expected = update["response"]!["body"]!["channelConsent"]!;
        JsonNode.DeepEquals(JsonNode.Parse(putBody)!["channelConsent"], expected)
            .Should().BeTrue(because: $"PUT answers channelConsent {expected.ToJsonString()}, and it was {putBody}");

        var get = await admin.SendAsync(RequestOf(read["request"]!.AsObject(), contact.ContactId.Value));
        var getBody = await get.Content.ReadAsStringAsync();
        ((int)get.StatusCode).Should().Be(read["status"]!.GetValue<int>());
        JsonNode.DeepEquals(JsonNode.Parse(getBody)!["channelConsent"], expected)
            .Should().BeTrue(because: $"GET answers channelConsent {expected.ToJsonString()}, and it was {getBody}");
    }
}
