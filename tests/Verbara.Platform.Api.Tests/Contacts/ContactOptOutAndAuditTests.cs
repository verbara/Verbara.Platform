using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Audit;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Contacts;

/// <summary>
/// contacts-writes-follow-their-permissions, task 1.5: tests written first, against today's code, for the opt-out route
/// (design D16) and the audit of every do-not-contact or consent change (design D17), fixtures/contact-opt-out.v1.json.
/// The route does not exist today (404) and no contact write records anything. Callers authenticate for real: JWTs this
/// host mints, and API keys through the API-key authentication handler as <c>Authorization: Bearer</c>.
/// </summary>
public sealed class ContactOptOutAndAuditTests : IClassFixture<ContactWritesApiFactory>
{
    private const string FixtureContactId = "7a1c0e2b5d3f4a6b8c9d0e1f2a3b4c5d";
    private const string UnknownContactId = "ffffffffffffffffffffffffffffffff";

    private static readonly JsonObject OptOut = ContactWritesApiFactory.Fixture("contact-opt-out.v1.json");
    private static readonly JsonObject Audit = OptOut["audit"]!.AsObject();

    private readonly ContactWritesApiFactory _factory;

    public ContactOptOutAndAuditTests(ContactWritesApiFactory factory) => _factory = factory;

    // ─── helpers ─────────────────────────────────────────────────────────────

    /// <summary>The fixture's contact before the opt-out: Voice and Sms addresses, Voice consent true, not marked.</summary>
    private static Contact FixtureContact(ContactWritesApiFactory factory, bool doNotContact = false)
    {
        var contact = factory.SeedContact(
            new ChannelAddress(ChannelType.Voice, "+15551234567"), new ChannelAddress(ChannelType.Sms, "+15551234567"));
        contact.FirstName = "Ana";
        contact.LastName = "Ruiz";
        contact.DoNotContact = doNotContact;
        contact.SetConsent(ChannelType.Voice, true);
        factory.SaveContact(contact);
        return contact;
    }

    private static HttpRequestMessage Request(JsonNode request, string? contactId, JsonNode? bodyOverride = null)
    {
        var path = request["path"]!.GetValue<string>();
        if (contactId is not null)
            path = path.Replace(FixtureContactId, contactId, StringComparison.Ordinal);
        var message = new HttpRequestMessage(new HttpMethod(request["method"]!.GetValue<string>()), path);
        if ((bodyOverride ?? request["body"]) is JsonNode body)
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return message;
    }

    private static HttpRequestMessage Put(Contact contact, string json) =>
        new(HttpMethod.Put, $"/api/v1/contacts/{contact.ContactId.Value}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static async Task<JsonNode?> ShouldBeAsync(HttpResponseMessage response, int status)
    {
        var body = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().Be(status, because: $"the response was {body}");
        return body.Length == 0 ? null : JsonNode.Parse(body);
    }

    private static JsonNode ChangeOf(object? value)
    {
        value.Should().BeOfType<string>("the change is pre-serialized JSON, never left to the store's fallback");
        return JsonNode.Parse((string)value!)!;
    }

    private static void ShouldBeTheRecord(AuditEntry entry, JsonObject expected, string actorId, string actorType, Contact contact)
    {
        entry.Category.Should().Be(expected["category"]!.GetValue<string>());
        entry.Action.Should().Be(expected["action"]!.GetValue<string>());
        entry.Severity.Should().Be(expected["severity"]!.GetValue<string>());
        entry.TargetType.Should().Be(expected["targetType"]!.GetValue<string>());
        entry.TargetId.Should().Be(contact.ContactId.Value);
        entry.ActorId.Should().Be(actorId);
        entry.ActorType.Should().Be(actorType);
        entry.RetainUntil.Should().Be(entry.OccurredAt.AddYears(5), "a contact record is kept five years");
    }

    private HttpClient AgentClient(out User user)
    {
        user = _factory.NewUser(UserRole.Agent, "opt-out-agent");
        return _factory.ClientFor(user);
    }

    // ─── the route (design D16) ──────────────────────────────────────────────

    [Fact]
    public async Task OptOut_ShouldMarkTheContactAndRecordTheNamedChannelsNo_WhenTheAgentTemplateSendsIt()
    {
        var contact = FixtureContact(_factory);
        using var agent = AgentClient(out var user);

        var body = await ShouldBeAsync(
            await agent.SendAsync(Request(OptOut["request"]!, contact.ContactId.Value)),
            OptOut["response"]!["status"]!.GetValue<int>());

        var expected = OptOut["response"]!["body"]!;
        body!["doNotContact"]!.GetValue<bool>().Should().Be(expected["doNotContact"]!.GetValue<bool>());
        JsonNode.DeepEquals(body["channelConsent"], expected["channelConsent"]).Should().BeTrue(body.ToJsonString());
        body["updatedBy"]!.GetValue<string>().Should().Be(user.UserId.Value);
        var stored = _factory.LoadContact(contact.ContactId)!;
        stored.DoNotContact.Should().BeTrue();
        stored.UpdatedBy.Should().Be(user.UserId.Value);
    }

    [Fact]
    public async Task OptOut_ShouldNeverClearDoNotContactAndWriteNoRecord_WhenTheBodySendsFalse()
    {
        var contact = FixtureContact(_factory, doNotContact: true);
        using var agent = AgentClient(out _);

        await ShouldBeAsync(
            await agent.SendAsync(Request(OptOut["request"]!, contact.ContactId.Value, new JsonObject { ["doNotContact"] = false })),
            200);

        _factory.LoadContact(contact.ContactId)!.DoNotContact.Should().BeTrue();
        _factory.ContactAuditOf(contact.ContactId).Should().BeEmpty("a call that changes nothing records nothing");
    }

    [Fact]
    public async Task OptOut_ShouldAnswerTheContactAndWriteNothing_WhenEveryNamedValueIsAlreadyInPlace()
    {
        var contact = FixtureContact(_factory, doNotContact: true);
        contact.SetConsent(ChannelType.Sms, false);
        _factory.SaveContact(contact);
        var before = _factory.LoadContact(contact.ContactId)!;
        var (updatedAt, updatedBy) = (before.UpdatedAt, before.UpdatedBy);
        using var agent = AgentClient(out _);

        var body = await ShouldBeAsync(await agent.SendAsync(Request(OptOut["request"]!, contact.ContactId.Value)), 200);

        body!["doNotContact"]!.GetValue<bool>().Should().BeTrue();
        JsonNode.DeepEquals(body["channelConsent"], new JsonObject { ["Voice"] = true, ["Sms"] = false })
            .Should().BeTrue(body.ToJsonString());
        var stored = _factory.LoadContact(contact.ContactId)!;
        stored.UpdatedAt.Should().Be(updatedAt, "a call that changes nothing writes nothing");
        stored.UpdatedBy.Should().Be(updatedBy);
        _factory.ContactAuditOf(contact.ContactId).Should().BeEmpty();
    }

    [Theory]
    [InlineData("quality_analyst")]
    [InlineData("api")]
    public async Task OptOut_ShouldRefuseNotPermitted_WhenTheCallersRolesHoldNoHandlePermission(string template)
    {
        var refusal = OptOut["refusals"]![0]!;
        var contact = FixtureContact(_factory);
        var user = _factory.NewUser(UserRole.Agent, $"opt-out-{template}");
        _factory.GrantTemplate(user, template);
        using var client = _factory.ClientFor(user);

        var body = await ShouldBeAsync(
            await client.SendAsync(Request(refusal["request"]!, contact.ContactId.Value)), refusal["status"]!.GetValue<int>());

        JsonNode.DeepEquals(body, refusal["body"]).Should().BeTrue(body?.ToJsonString());
        _factory.LoadContact(contact.ContactId)!.DoNotContact.Should().BeFalse();
        _factory.ContactAuditOf(contact.ContactId).Should().BeEmpty();

        // Evaluated first: an unknown channel and an unknown id answer the same 403.
        var badChannels = OptOut["refusals"]![1]!["request"]!;
        await ShouldBeAsync(await client.SendAsync(Request(badChannels, UnknownContactId)), 403);
    }

    [Fact]
    public async Task OptOut_ShouldRefuseUnknownConsentChannelAndWriteNothing_WhenAChannelIsNotAChannelName()
    {
        var refusal = OptOut["refusals"]![1]!;
        var contact = FixtureContact(_factory);
        using var agent = AgentClient(out _);

        var body = await ShouldBeAsync(
            await agent.SendAsync(Request(refusal["request"]!, contact.ContactId.Value)), refusal["status"]!.GetValue<int>());

        JsonNode.DeepEquals(body, refusal["body"]).Should().BeTrue(body?.ToJsonString());
        var stored = _factory.LoadContact(contact.ContactId)!;
        stored.DoNotContact.Should().BeFalse();
        stored.ChannelConsent.Should().NotContainKey(ChannelType.Sms);
        _factory.ContactAuditOf(contact.ContactId).Should().BeEmpty();

        // Validated before the lookup: an unknown id with the same body answers the same 400.
        await ShouldBeAsync(await agent.SendAsync(Request(refusal["request"]!, UnknownContactId)), 400);
    }

    [Fact]
    public async Task OptOut_ShouldAnswer404WithNoBody_WhenNoContactHasTheId()
    {
        var refusal = OptOut["refusals"]![2]!;
        using var agent = AgentClient(out _);

        var body = await ShouldBeAsync(await agent.SendAsync(Request(refusal["request"]!, null)), refusal["status"]!.GetValue<int>());

        body.Should().BeNull();
    }

    // ─── the audit (design D17) ──────────────────────────────────────────────

    [Fact]
    public async Task OptOut_ShouldWriteOneOptedOutRecord_WithTheTwoMembersBeforeAndAfter()
    {
        var expected = Audit["optOut"]!.AsObject();
        var contact = FixtureContact(_factory);
        using var agent = AgentClient(out var user);

        await ShouldBeAsync(await agent.SendAsync(Request(OptOut["request"]!, contact.ContactId.Value)), 200);

        var entry = _factory.ContactAuditOf(contact.ContactId).Should().ContainSingle().Subject;
        ShouldBeTheRecord(entry, expected, user.UserId.Value, "user", contact);
        JsonNode.DeepEquals(ChangeOf(entry.Changes!.Before), expected["changes"]!["before"]).Should().BeTrue();
        JsonNode.DeepEquals(ChangeOf(entry.Changes!.After), expected["changes"]!["after"]).Should().BeTrue();
    }

    [Fact]
    public async Task UpdateContact_ShouldWriteOneConsentChangedRecord_WhenChannelConsentChanges()
    {
        var expected = Audit["update"]!.AsObject();
        var contact = _factory.SeedContact(new ChannelAddress(ChannelType.Voice, "+15559990001"));
        var admin = _factory.NewUser(UserRole.Admin, "consent-admin");
        using var client = _factory.ClientFor(admin);
        var consent = ContactWritesApiFactory.Fixture("contact-channel-consent.v1.json")["update"]!["request"]!["body"]!["channelConsent"]!;

        var body = await ShouldBeAsync(
            await client.SendAsync(Put(contact, new JsonObject { ["channelConsent"] = consent.DeepClone() }.ToJsonString())), 200);

        var entry = _factory.ContactAuditOf(contact.ContactId).Should().ContainSingle().Subject;
        ShouldBeTheRecord(entry, expected, admin.UserId.Value, "user", contact);
        JsonNode.DeepEquals(ChangeOf(entry.Changes!.Before)["channelConsent"], new JsonObject()).Should().BeTrue();
        JsonNode.DeepEquals(ChangeOf(entry.Changes!.After)["channelConsent"], consent).Should().BeTrue();
        body!["updatedBy"]!.GetValue<string>().Should().Be(admin.UserId.Value);
    }

    [Fact]
    public async Task UpdateContact_ShouldWriteNoRecord_WhenNeitherMemberChanges()
    {
        var contact = _factory.SeedContact(new ChannelAddress(ChannelType.Voice, "+15559990002"));
        using var client = _factory.ClientFor(_factory.NewUser(UserRole.Admin, "name-admin"));

        await ShouldBeAsync(await client.SendAsync(Put(contact, """{"firstName":"Ana","doNotContact":false}""")), 200);

        _factory.ContactAuditOf(contact.ContactId).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteContact_ShouldWriteOneDeletedRecord_WithDoNotContactInItsMetadataAndNoAddress()
    {
        var expected = Audit["delete"]!.AsObject();
        var contact = FixtureContact(_factory, doNotContact: true);
        var admin = _factory.NewUser(UserRole.Admin, "delete-admin");
        using var client = _factory.ClientFor(admin);

        await ShouldBeAsync(await client.DeleteAsync($"/api/v1/contacts/{contact.ContactId.Value}"), 204);

        var entry = _factory.ContactAuditOf(contact.ContactId).Should().ContainSingle().Subject;
        ShouldBeTheRecord(entry, expected, admin.UserId.Value, "user", contact);
        entry.Metadata.Should().BeEquivalentTo(
            expected["metadata"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()));
        $"{entry.Changes?.Before}{entry.Changes?.After}".Should().NotContain("+15551234567");
    }

    [Fact]
    public async Task OptOutService_ShouldRecordTheSystemActor_WhenCalledWithNoHttpCaller()
    {
        // fixture `audit.actorResolution.service`: no HTTP caller, so the actor is the platform itself.
        var expected = Audit["actorResolution"]!["service"]!;
        var contact = FixtureContact(_factory);
        var service = _factory.Services.GetRequiredService<IContactOptOutService>();

        var result = await service.OptOutAsync(
            ContactWritesApiFactory.Tenant, contact.ContactId, doNotContact: true, [ChannelType.Sms], ContactActor.System, CancellationToken.None);

        result.Should().NotBeNull();
        var stored = _factory.LoadContact(contact.ContactId)!;
        stored.DoNotContact.Should().BeTrue();
        stored.ChannelConsent.Should().Contain(ChannelType.Sms, false);
        stored.UpdatedBy.Should().Be(expected["updatedBy"]!.GetValue<string>());
        var entry = _factory.ContactAuditOf(contact.ContactId).Should().ContainSingle().Subject;
        entry.Action.Should().Be(Audit["optOut"]!["action"]!.GetValue<string>());
        entry.ActorId.Should().Be(expected["actorId"]!.GetValue<string>());
        entry.ActorType.Should().Be(expected["actorType"]!.GetValue<string>());
    }

    // ─── the actor through real authentication (second spec audit F1) ────────

    [Fact]
    public async Task UpdateContact_ShouldAttributeTheRecordToTheKeysOwner_WhenAKeyBoundToAnApiUserChangesConsent()
    {
        var expected = Audit["actorResolution"]!["apiKeyBoundToUser"]!;
        var contact = _factory.SeedContact(new ChannelAddress(ChannelType.Email, "ana@example.com"));
        var owner = _factory.NewUser(UserRole.Api, "integration");
        using var key = _factory.UserBoundApiKeyClient(owner);

        var body = await ShouldBeAsync(await key.SendAsync(Put(contact, """{"channelConsent":{"Email":false}}""")), 200);

        var entry = _factory.ContactAuditOf(contact.ContactId).Should().ContainSingle().Subject;
        entry.Action.Should().Be(Audit["update"]!["action"]!.GetValue<string>());
        entry.ActorId.Should().Be(owner.UserId.Value, "never `system`, never the key id");
        entry.ActorType.Should().Be(expected["actorType"]!.GetValue<string>());
        body!["updatedBy"]!.GetValue<string>().Should().Be(owner.UserId.Value);
    }

    [Fact]
    public async Task OptOut_ShouldAttributeTheRecordToTheKey_WhenAManagementKeyBoundToNoUserSendsIt()
    {
        var expected = Audit["actorResolution"]!["apiKeyUnbound"]!;
        var contact = FixtureContact(_factory);
        using var key = _factory.UnboundManagementKeyClient(out var keyId);

        var body = await ShouldBeAsync(
            await key.SendAsync(Request(OptOut["request"]!, contact.ContactId.Value, new JsonObject { ["doNotContact"] = true })),
            200);

        var entry = _factory.ContactAuditOf(contact.ContactId).Should().ContainSingle().Subject;
        entry.Action.Should().Be(Audit["optOut"]!["action"]!.GetValue<string>());
        entry.ActorId.Should().Be(keyId);
        entry.ActorType.Should().Be(expected["actorType"]!.GetValue<string>());
        body!["updatedBy"]!.GetValue<string>().Should().Be(keyId);
    }

    // ─── atomicity (design D17, fourth spec audit F1) ────────────────────────

    [Fact]
    public async Task UpdateContact_ShouldLeaveTheContactUnchangedThenApplyOnceOnRetry_WhenTheAuditInsertFails()
    {
        await using var factory = new ContactWritesApiFactory();
        var contact = factory.SeedContact(new ChannelAddress(ChannelType.Voice, "+15559990003"));
        using var client = factory.ClientFor(factory.NewUser(UserRole.Admin, "atomic-admin"));
        factory.Faults.FailNextAuditSave();

        var failed = await client.SendAsync(Put(contact, """{"doNotContact":true}"""));

        ((int)failed.StatusCode).Should().BeGreaterThanOrEqualTo(500, await failed.Content.ReadAsStringAsync());
        factory.LoadContact(contact.ContactId)!.DoNotContact.Should().BeFalse("the change and its record persist together or not at all");
        factory.ContactAuditOf(contact.ContactId).Should().BeEmpty();

        await ShouldBeAsync(await client.SendAsync(Put(contact, """{"doNotContact":true}""")), 200);

        factory.LoadContact(contact.ContactId)!.DoNotContact.Should().BeTrue();
        factory.ContactAuditOf(contact.ContactId).Should().ContainSingle(e => e.Action == "contact.consent_changed");
    }

    [Fact]
    public async Task OptOut_ShouldWriteNoRecord_WhenTheContactSaveFails()
    {
        await using var factory = new ContactWritesApiFactory();
        var contact = FixtureContact(factory);
        using var agent = factory.ClientFor(factory.NewUser(UserRole.Agent, "atomic-agent"));
        factory.Faults.FailNextContactSave();

        var failed = await agent.SendAsync(Request(OptOut["request"]!, contact.ContactId.Value));

        ((int)failed.StatusCode).Should().BeGreaterThanOrEqualTo(500, await failed.Content.ReadAsStringAsync());
        factory.LoadContact(contact.ContactId)!.DoNotContact.Should().BeFalse();
        factory.ContactAuditOf(contact.ContactId).Should().BeEmpty();
    }
}
