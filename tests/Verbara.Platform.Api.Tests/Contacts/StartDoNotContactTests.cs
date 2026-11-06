using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;

namespace Verbara.Platform.Api.Tests.Contacts;

/// <summary>
/// contacts-writes-follow-their-permissions, task 1.2 (start side): regression tests written first, against the unfixed
/// code (design D11, D13; fixtures/do-not-contact-refusals.v1.json <c>start</c>, <c>startConsentWithdrawn</c>,
/// <c>startReusesOwnOpenConversation</c>, <c>notRefused</c>), through the real host. Each contact wrote on WhatsApp an
/// hour ago, so the customer-service window is open and no template is needed: the only refusal a start can meet here
/// is the one under test. A refused start sends nothing, stores no conversation and reserves no capacity; the reuse of the
/// caller's own open, sendable conversation is never refused.
/// </summary>
public sealed class StartDoNotContactTests : IClassFixture<AgentStartedConversationEndpointTests.AgentStartApiFactory>
{
    private static readonly JsonObject Fixture = ContactWritesApiFactory.Fixture("do-not-contact-refusals.v1.json");

    private readonly AgentStartedConversationEndpointTests.AgentStartApiFactory _factory;

    public StartDoNotContactTests(AgentStartedConversationEndpointTests.AgentStartApiFactory factory) => _factory = factory;

    private async Task<(Contact Contact, string Address)> SeedContactAsync(bool doNotContact, JsonNode? consent, string? sharedAddress = null)
    {
        await _factory.EnsureWhatsAppConfiguredAsync();
        var contact = new Contact
        {
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var address = sharedAddress ?? $"1555{Random.Shared.Next(1_000_000, 9_999_999)}";
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, address));
        contact.DoNotContact = doNotContact;
        foreach (var (channel, value) in consent?.AsObject() ?? [])
            contact.SetConsent(Enum.Parse<ChannelType>(channel), value!.GetValue<bool>());
        var services = _factory.Services;
        await services.GetRequiredService<IContactStore>().SaveAsync(contact, CancellationToken.None);

        // An inbound WhatsApp message an hour ago, in a closed conversation: the window is open.
        var earlier = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            ContactId = contact.ContactId,
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Closed,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
        };
        await services.GetRequiredService<IConversationStore>().SaveAsync(earlier, CancellationToken.None);
        await services.GetRequiredService<IMessageStore>().SaveAsync(new Message
        {
            MessageId = EntityId.From($"msg-{Guid.NewGuid():N}"),
            ConversationId = earlier.ConversationId,
            TenantId = ConversationOwnershipApiFactory.Tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([new TextBlock("Hi")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        }, CancellationToken.None);
        return (contact, address);
    }

    private static HttpRequestMessage RequestFor(JsonNode request, Contact contact)
    {
        var body = request["body"]!.DeepClone().AsObject();
        body["contactId"] = contact.ContactId.Value;
        return new HttpRequestMessage(new HttpMethod(request["method"]!.GetValue<string>()), request["path"]!.GetValue<string>())
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    private async Task<IReadOnlyList<Conversation>> OpenConversationsAsync(Contact contact) =>
        (await _factory.Services.GetRequiredService<IConversationStore>().ListByContactAsync(
            ConversationOwnershipApiFactory.Tenant, contact.ContactId, CancellationToken.None))
        .Where(c => c.State != ConversationState.Closed)
        .ToList();

    private async Task ShouldRefuseAsync(JsonNode fixtureCase, bool doNotContact, JsonNode? consent, bool onATwin = false)
    {
        var (user, agent) = _factory.NewAgent("dnc-starter");
        var (contact, address) = onATwin
            ? await SeedContactAsync(doNotContact: false, consent: null)
            : await SeedContactAsync(doNotContact, consent);
        if (onATwin)
        {
            // Another contact of the tenant holds the same WhatsApp number, written differently, and carries the
            // restriction; the request names the unmarked one.
            await SeedContactAsync(doNotContact, consent, sharedAddress: $"{address[..1]} ({address[1..4]}) {address[4..]}");
        }
        using var client = _factory.ClientFor(user);

        var response = await client.SendAsync(RequestFor(fixtureCase["request"]!, contact));

        var body = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().Be(fixtureCase["status"]!.GetValue<int>(), because: $"the response was {body}");
        JsonNode.DeepEquals(JsonNode.Parse(body), fixtureCase["body"])
            .Should().BeTrue(because: $"the body is {fixtureCase["body"]!.ToJsonString()}, and it was {body}");
        _factory.Graph.RequestsTo(address).Should().BeEmpty("a refused start sends nothing");
        (await OpenConversationsAsync(contact)).Should().BeEmpty("a refused start stores no conversation");
        _factory.LoadOf(agent.AgentId, ChannelType.WhatsApp).Should().Be(0, "a refused start reserves no capacity");
    }

    [Fact]
    public async Task CreateConversation_ShouldRefuseDoNotContact_WhenTheContactIsMarked()
    {
        var start = Fixture["start"]!;
        await ShouldRefuseAsync(start, doNotContact: true, consent: null);
    }

    [Fact]
    public async Task CreateConversation_ShouldRefuseConsentWithdrawn_WhenTheChannelsConsentIsRecordedFalse()
    {
        var start = Fixture["startConsentWithdrawn"]!;
        await ShouldRefuseAsync(start, doNotContact: false, start["contactChannelConsent"]);
    }

    [Fact]
    public async Task CreateConversation_ShouldRefuseDoNotContact_WhenTheContactIsMarkedAndItsConsentIsRecordedFalse()
    {
        await ShouldRefuseAsync(
            Fixture["start"]!, doNotContact: true, Fixture["startConsentWithdrawn"]!["contactChannelConsent"]);
    }

    [Fact]
    public async Task CreateConversation_ShouldReuseTheOwnOpenConversation_WhenTheContactIsMarkedAndRecordsNo()
    {
        var reuse = Fixture["startReusesOwnOpenConversation"]!;
        var (user, agent) = _factory.NewAgent("dnc-reuser");
        var (contact, address) = await SeedContactAsync(doNotContact: true, reuse["contactChannelConsent"]);
        var open = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            ContactId = contact.ContactId,
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(agent.AgentId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.Services.GetRequiredService<IConversationStore>().SaveAsync(open, CancellationToken.None);
        using var client = _factory.ClientFor(user);

        var response = await client.SendAsync(RequestFor(reuse["request"]!, contact));

        ((int)response.StatusCode).Should().Be(reuse["status"]!.GetValue<int>(), await response.Content.ReadAsStringAsync());
        response.Headers.Location!.ToString().Should().EndWith($"/conversations/{open.ConversationId.Value}");
        _factory.Graph.RequestsTo(address).Should().ContainSingle("the reused conversation is sent on");
    }

    [Fact]
    public async Task CreateConversation_ShouldStart_WhenTheChannelHasNoRecordedNoAndTheContactIsNotMarked()
    {
        foreach (var example in Fixture["notRefused"]!["examples"]!.AsArray())
        {
            example!["refused"]!.GetValue<bool>().Should().BeFalse();
            var (user, _) = _factory.NewAgent("consent-starter");
            var (contact, address) = await SeedContactAsync(doNotContact: false, example["contactChannelConsent"]);
            using var client = _factory.ClientFor(user);
            var request = Fixture["start"]!["request"]!.DeepClone();
            request["body"]!["channel"] = example["channel"]!.GetValue<string>();

            var response = await client.SendAsync(RequestFor(request, contact));

            response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            _factory.Graph.RequestsTo(address).Should().ContainSingle();
        }
    }

    // ─── review round: a duplicate contact holding the same number ───────────

    [Fact]
    public async Task CreateConversation_ShouldRefuseDoNotContact_WhenAnotherContactHoldingTheNumberIsMarked()
    {
        await ShouldRefuseAsync(Fixture["start"]!, doNotContact: true, consent: null, onATwin: true);
    }

    [Fact]
    public async Task CreateConversation_ShouldRefuseConsentWithdrawn_WhenAnotherContactHoldingTheNumberRecordsNo()
    {
        var start = Fixture["startConsentWithdrawn"]!;
        await ShouldRefuseAsync(start, doNotContact: false, start["contactChannelConsent"], onATwin: true);
    }

    [Fact]
    public async Task CreateConversation_ShouldReuseTheOwnOpenConversation_WhenAnotherContactHoldingTheNumberIsMarked()
    {
        var reuse = Fixture["startReusesOwnOpenConversation"]!;
        var (user, agent) = _factory.NewAgent("twin-reuser");
        var (contact, address) = await SeedContactAsync(doNotContact: false, consent: null);
        await SeedContactAsync(doNotContact: true, reuse["contactChannelConsent"], sharedAddress: address);
        var open = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            ContactId = contact.ContactId,
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(agent.AgentId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.Services.GetRequiredService<IConversationStore>().SaveAsync(open, CancellationToken.None);
        using var client = _factory.ClientFor(user);

        var response = await client.SendAsync(RequestFor(reuse["request"]!, contact));

        ((int)response.StatusCode).Should().Be(reuse["status"]!.GetValue<int>(), await response.Content.ReadAsStringAsync());
        response.Headers.Location!.ToString().Should().EndWith($"/conversations/{open.ConversationId.Value}");
    }
}
