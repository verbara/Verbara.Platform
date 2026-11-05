using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Contacts;

/// <summary>
/// contacts-writes-follow-their-permissions, task 2.2: <c>channelConsent</c> on <c>PUT /contacts/{id}</c> (design D8;
/// fixtures/contact-channel-consent.v1.json). Keys are channel names as <c>GET</c> writes them, compared ordinally; a
/// key that is not one refuses the whole write with 400 <c>unknown-consent-channel</c> before the lookup; accepted keys
/// merge into the stored map; an absent or null member changes nothing; the permission is evaluated before the body.
/// </summary>
public sealed class ContactChannelConsentTests : IClassFixture<ContactWritesApiFactory>
{
    private const string FixtureContactId = "7a1c0e2b5d3f4a6b8c9d0e1f2a3b4c5d";
    private const string UnknownContactId = "ffffffffffffffffffffffffffffffff";

    private static readonly JsonObject Consent = ContactWritesApiFactory.Fixture("contact-channel-consent.v1.json");
    private static readonly JsonObject Refusals = ContactWritesApiFactory.Fixture("contact-write-refusals.v1.json");

    private readonly ContactWritesApiFactory _factory;

    public ContactChannelConsentTests(ContactWritesApiFactory factory) => _factory = factory;

    private static JsonObject ConsentRefusal => Consent["refusals"]![0]!.AsObject();

    private Contact SeedContact(params (ChannelType Channel, bool Consented)[] consent)
    {
        var contact = _factory.SeedContact(new ChannelAddress(ChannelType.Voice, $"+1555{Random.Shared.Next(1000000, 9999999)}"));
        contact.FirstName = "Ana";
        contact.LastName = "Ruiz";
        foreach (var (channel, consented) in consent)
            contact.SetConsent(channel, consented);
        _factory.SaveContact(contact);
        return contact;
    }

    private HttpClient EditorClient() => _factory.ClientFor(_factory.NewUser(UserRole.Admin, "consent-editor"));

    private static HttpRequestMessage RequestOf(JsonObject request, string contactId, JsonNode? bodyOverride = null)
    {
        var path = request["path"]!.GetValue<string>().Replace(FixtureContactId, contactId, StringComparison.Ordinal);
        var message = new HttpRequestMessage(new HttpMethod(request["method"]!.GetValue<string>()), path);
        var body = bodyOverride ?? request["body"];
        if (body is not null)
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return message;
    }

    private static HttpRequestMessage Put(string contactId, string json) =>
        new(HttpMethod.Put, $"/api/v1/contacts/{contactId}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static async Task<JsonNode> ShouldAnswerAsync(HttpResponseMessage response, int status)
    {
        var body = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().Be(status, because: $"the response was {body}");
        return JsonNode.Parse(body.Length == 0 ? "null" : body)!;
    }

    private static void ShouldEqual(JsonNode? actual, JsonNode? expected, string what) =>
        JsonNode.DeepEquals(actual, expected)
            .Should().BeTrue(because: $"{what} is {expected?.ToJsonString()}, and it was {actual?.ToJsonString()}");

    private static string ConsentOf(Contact? contact) =>
        contact is null
            ? "<deleted>"
            : $"{contact.DoNotContact}|{contact.UpdatedAt:O}|" +
              string.Join(',', contact.ChannelConsent.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

    [Fact]
    public async Task UpdateContact_ShouldMergeConsentIntoTheStoredMap_WhenALaterUpdateNamesOneChannel()
    {
        var update = Consent["update"]!.AsObject();
        var merge = Consent["merge"]!.AsObject();
        var read = Consent["read"]!.AsObject();
        var contact = SeedContact();
        using var editor = EditorClient();

        var first = await ShouldAnswerAsync(
            await editor.SendAsync(RequestOf(update["request"]!.AsObject(), contact.ContactId.Value)),
            update["response"]!["status"]!.GetValue<int>());
        ShouldEqual(first["channelConsent"], update["response"]!["body"]!["channelConsent"], "the first PUT's channelConsent");

        var second = await ShouldAnswerAsync(
            await editor.SendAsync(RequestOf(merge["request"]!.AsObject(), contact.ContactId.Value)), 200);
        ShouldEqual(second["channelConsent"], merge["responseChannelConsent"], "the merging PUT's channelConsent");

        var got = await ShouldAnswerAsync(
            await editor.SendAsync(RequestOf(read["request"]!.AsObject(), contact.ContactId.Value)),
            read["status"]!.GetValue<int>());
        ShouldEqual(got["channelConsent"], read["bodyChannelConsent"], "GET's channelConsent");
    }

    [Theory]
    [InlineData("""{"doNotContact": true}""")]
    [InlineData("""{"doNotContact": true, "channelConsent": null}""")]
    public async Task UpdateContact_ShouldKeepTheStoredConsent_WhenTheMemberIsAbsentOrNull(string json)
    {
        var contact = SeedContact((ChannelType.Voice, true));
        using var editor = EditorClient();

        var body = await ShouldAnswerAsync(await editor.SendAsync(Put(contact.ContactId.Value, json)), 200);

        body["doNotContact"]!.GetValue<bool>().Should().BeTrue();
        ShouldEqual(body["channelConsent"], JsonNode.Parse("""{"Voice": true}"""), "the returned channelConsent");
        _factory.LoadContact(contact.ContactId)!.ChannelConsent.Should().Equal(
            new Dictionary<ChannelType, bool> { [ChannelType.Voice] = true });
    }

    [Fact]
    public async Task UpdateContact_ShouldRefuseUnknownConsentChannelAndWriteNothing_WhenAKeyIsNotAChannelName()
    {
        var refusal = ConsentRefusal;
        var contact = SeedContact((ChannelType.Voice, false));
        var before = ConsentOf(_factory.LoadContact(contact.ContactId));
        using var editor = EditorClient();

        var body = await ShouldAnswerAsync(
            await editor.SendAsync(RequestOf(refusal["request"]!.AsObject(), contact.ContactId.Value)),
            refusal["status"]!.GetValue<int>());

        ShouldEqual(body, refusal["body"], "the refusal");
        ConsentOf(_factory.LoadContact(contact.ContactId)).Should().Be(before, because: "a refused update writes nothing, doNotContact included");
    }

    [Fact]
    public async Task UpdateContact_ShouldRefuseUnknownConsentChannelBeforeTheLookup_WhenTheIdIsUnknown()
    {
        var refusal = ConsentRefusal;
        using var editor = EditorClient();

        var body = await ShouldAnswerAsync(
            await editor.SendAsync(RequestOf(refusal["request"]!.AsObject(), UnknownContactId)),
            refusal["status"]!.GetValue<int>());

        ShouldEqual(body, refusal["body"], "the refusal");
    }

    [Theory]
    [InlineData("""{"channelConsent": {"VOICE": true}}""", "VOICE")]
    [InlineData("""{"channelConsent": {"Voice ": true}}""", "Voice ")]
    [InlineData("""{"channelConsent": {"10": true}}""", "10")]
    [InlineData("""{"channelConsent": {"": true}}""", "")]
    [InlineData("""{"channelConsent": {"Voice,Sms": true}}""", "Voice,Sms")]
    public async Task UpdateContact_ShouldRefuseUnknownConsentChannel_WhenAKeyOnlyResemblesAChannelName(string json, string key)
    {
        var contact = SeedContact();
        var before = ConsentOf(_factory.LoadContact(contact.ContactId));
        using var editor = EditorClient();

        var body = await ShouldAnswerAsync(await editor.SendAsync(Put(contact.ContactId.Value, json)), 400);

        ShouldEqual(body, new JsonObject { ["error"] = "unknown-consent-channel", ["details"] = new JsonArray(key) }, "the refusal");
        ConsentOf(_factory.LoadContact(contact.ContactId)).Should().Be(before);
    }

    [Fact]
    public async Task UpdateContact_ShouldAcceptEveryChannelName_WhenEachIsWrittenAsGetWritesIt()
    {
        var contact = SeedContact();
        using var editor = EditorClient();
        var names = Enum.GetNames<ChannelType>();
        var member = new JsonObject();
        foreach (var name in names)
            member[name] = true;

        var body = await ShouldAnswerAsync(
            await editor.SendAsync(Put(contact.ContactId.Value, new JsonObject { ["channelConsent"] = member }.ToJsonString())), 200);

        ShouldEqual(body["channelConsent"], member, "the returned channelConsent");
    }

    [Fact]
    public async Task UpdateContact_ShouldRefuseNotPermittedBeforeValidatingConsent_WhenTheCallerHoldsOnlyTheAgentTemplate()
    {
        var refusal = ConsentRefusal;
        var notPermitted = Refusals["refusals"]![1]!.AsObject();
        var contact = SeedContact((ChannelType.Voice, true));
        var before = ConsentOf(_factory.LoadContact(contact.ContactId));
        using var agent = _factory.ClientFor(_factory.NewUser(UserRole.Agent, "consent-agent"));

        var body = await ShouldAnswerAsync(
            await agent.SendAsync(RequestOf(refusal["request"]!.AsObject(), contact.ContactId.Value)),
            notPermitted["status"]!.GetValue<int>());

        ShouldEqual(body, notPermitted["body"], "the refusal");
        ConsentOf(_factory.LoadContact(contact.ContactId)).Should().Be(before);
    }
}
