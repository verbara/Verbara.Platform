using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WhatsApp;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Storage.InMemory;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// agent-started-conversations (design D14) through the real host: <c>POST /conversations</c> with an
/// <c>initialMessage</c> sends first and persists nothing when the provider rejects the send
/// (<c>502 channel-send-failed</c>); a success is born <c>Active</c> and owned by the caller's Agent profile
/// with the message stored <c>Sent</c> and no licensed-agent ledger row; an open conversation owned by another agent
/// is refused with <c>409 conversation-open-elsewhere</c>, the caller's own one that takes no message with
/// <c>409 conversation-not-sendable</c>. Without an initial message the deprecated queued path stays.
/// </summary>
public sealed class AgentStartedConversationEndpointTests
    : IClassFixture<AgentStartedConversationEndpointTests.AgentStartApiFactory>
{
    private const string PhoneNumberId = "106540352242977";
    private const string AccessToken = "EAAagentstart";
    private const string Template = "hello_world";

    private readonly AgentStartApiFactory _factory;

    public AgentStartedConversationEndpointTests(AgentStartApiFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateConversation_ShouldReturn201ActiveOwned_WhenInitialMessage()
    {
        var (user, agent) = _factory.NewAgent("starter");
        var (contactId, address) = await SeedWhatsAppContactAsync();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Hello!"));

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        var conversationId = EntityId.From(response.Headers.Location!.ToString().Split('/')[^1]);
        var stored = _factory.Load(conversationId)!;
        stored.State.Should().Be(ConversationState.Active);
        stored.Owner.Should().Be(ConversationOwner.ForAgent(agent.AgentId));
        var message = (await OutboundMessagesAsync(conversationId)).Should().ContainSingle().Subject;
        message.DeliveryStatus.Should().Be(MessageDeliveryStatus.Sent);
        message.ExternalMessageId.Should().NotBeNullOrEmpty();
        _factory.Graph.RequestsTo(address).Should().ContainSingle();
    }

    [Fact]
    public async Task CreateConversation_ShouldLeaveNoOrphan_WhenSendFails()
    {
        var (user, agent) = _factory.NewAgent("starter");
        var (contactId, address) = await SeedWhatsAppContactAsync();
        _factory.Graph.Reject(address);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Hello!"));

        // The orphan is checked first: it is the v2.27.0 residual this block closes.
        (await OpenConversationAsync(contactId)).Should().BeNull("a failed first send persists no conversation");
        await ShouldBeErrorAsync(response, HttpStatusCode.BadGateway, "channel-send-failed");
        (await ConversationsOfAsync(contactId)).Should().BeEmpty("no conversation of any state is stored");
        _factory.LoadOf(agent.AgentId, ChannelType.WhatsApp).Should().Be(0, "no capacity stays reserved");
    }

    [Fact]
    public async Task CreateConversation_ShouldReturn409Code_WhenOpenElsewhere()
    {
        var (user, _) = _factory.NewAgent("starter");
        var (_, otherAgent) = _factory.NewAgent("other");
        var (contactId, address) = await SeedWhatsAppContactAsync();
        var open = await SeedOpenConversationAsync(contactId, ConversationOwner.ForAgent(otherAgent.AgentId));
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Hello!"));

        var body = await ShouldBeErrorAsync(response, HttpStatusCode.Conflict, "conversation-open-elsewhere");
        using (var json = JsonDocument.Parse(body))
        {
            json.RootElement.GetProperty("conversationId").GetString().Should().Be(open.ConversationId.Value);
            json.RootElement.GetProperty("ownerKind").GetString().Should().Be("Agent");
        }
        _factory.Load(open.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(otherAgent.AgentId));
        _factory.Graph.RequestsTo(address).Should().BeEmpty("nothing is sent");
    }

    [Theory]
    [InlineData(ChannelType.WhatsApp)]
    [InlineData(ChannelType.WebChat)]
    public async Task CreateConversation_ShouldReturn201Queued_WhenNoInitialMessage(ChannelType channel)
    {
        var (user, _) = _factory.NewAgent("starter");
        var (contactId, _) = await SeedWhatsAppContactAsync();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            "/api/v1/conversations", new { contactId = contactId.Value, channel = channel.ToString() });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var stored = (await _factory.Services.GetRequiredService<IConversationStore>().FindActiveByContactAsync(
            ConversationOwnershipApiFactory.Tenant, contactId, channel, CancellationToken.None))!;
        stored.State.Should().Be(ConversationState.Queued);
        stored.Owner.Should().BeNull();
    }

    [Fact]
    public async Task CreateConversation_ShouldAuditWithoutLedgerRow_WhenStarted()
    {
        var (user, agent) = _factory.NewAgent("starter");
        var (contactId, _) = await SeedWhatsAppContactAsync();
        var ledger = _factory.Services.GetRequiredService<InMemoryLicenseAgentLedger>();
        var before = ledger.Events(ConversationOwnershipApiFactory.Tenant.Value).Count;
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Hello!"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var conversationId = EntityId.From(response.Headers.Location!.ToString().Split('/')[^1]);
        _factory.AuditOf(conversationId).Should().ContainSingle(e => e.Action == "conversation.started");
        var events = ledger.Events(ConversationOwnershipApiFactory.Tenant.Value);
        events.Should().NotContain(e => e.ConversationId == conversationId.Value,
            "an agent starting a conversation in its own name is not an ownership move");
        events.Count.Should().Be(before, "the start appends no licensed-agent ledger row");
        _factory.LoadOf(agent.AgentId, ChannelType.WhatsApp).Should().Be(1);
    }

    [Theory]
    [InlineData(ConversationState.WrapUp)]
    [InlineData(ConversationState.Snoozed)]
    public async Task CreateConversation_ShouldReturn409NotSendable_WhenCallerOwnsConversationInState(ConversationState state)
    {
        var (user, agent) = _factory.NewAgent("starter");
        var (contactId, address) = await SeedWhatsAppContactAsync();
        var open = await SeedOpenConversationAsync(contactId, ConversationOwner.ForAgent(agent.AgentId), state);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Hello!"));

        var body = await ShouldBeErrorAsync(response, HttpStatusCode.Conflict, "conversation-not-sendable");
        using (var json = JsonDocument.Parse(body))
        {
            json.RootElement.GetProperty("conversationId").GetString().Should().Be(open.ConversationId.Value);
            json.RootElement.GetProperty("ownerKind").GetString().Should().Be("Agent");
        }
        _factory.Load(open.ConversationId)!.State.Should().Be(state);
        _factory.Graph.RequestsTo(address).Should().BeEmpty("nothing is sent");
    }

    [Fact]
    public async Task CreateConversation_ShouldSendFreeForm_WhenContactWroteWithin24hInAnotherConversation()
    {
        var (user, agent) = _factory.NewAgent("starter");
        var (contactId, address) = await SeedWhatsAppContactAsync();
        var earlier = await SeedOpenConversationAsync(contactId, owner: null, ConversationState.Closed);
        await _factory.Services.GetRequiredService<IMessageStore>().SaveAsync(new Message
        {
            MessageId = EntityId.From($"msg-{Guid.NewGuid():N}"),
            ConversationId = earlier.ConversationId,
            TenantId = ConversationOwnershipApiFactory.Tenant,
            Direction = MessageDirection.Inbound,
            Channel = ChannelType.WhatsApp,
            Content = new MessageEnvelope([new TextBlock("Can someone call me back?")]),
            DeliveryStatus = MessageDeliveryStatus.Delivered,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        }, CancellationToken.None);
        using var client = _factory.ClientFor(user);

        // WhatsApp's customer-service window belongs to the contact, not to one conversation: no template needed.
        var response = await client.PostAsJsonAsync(
            "/api/v1/conversations", new { contactId = contactId.Value, channel = "WhatsApp", initialMessage = "Hi again" });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var request = _factory.Graph.RequestsTo(address).Should().ContainSingle().Subject;
        request.Should().Contain("\"type\":\"text\"").And.NotContain("\"template\"");
        _factory.LoadOf(agent.AgentId, ChannelType.WhatsApp).Should().Be(1);
    }

    [Fact]
    public async Task CreateConversation_ShouldReturn409AtCapacity_WhenAgentHasNoChatCapacityLeft()
    {
        var (user, agent) = _factory.NewAgent("starter", capacity: new ChannelCapacityOverride { MaxChat = 1 });
        _factory.Reserve(agent.AgentId, ChannelType.WhatsApp);
        var (contactId, address) = await SeedWhatsAppContactAsync();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Hello!"));

        await ShouldBeErrorAsync(response, HttpStatusCode.Conflict, "agent-at-capacity");
        (await ConversationsOfAsync(contactId)).Should().BeEmpty();
        _factory.Graph.RequestsTo(address).Should().BeEmpty();
        _factory.LoadOf(agent.AgentId, ChannelType.WhatsApp).Should().Be(1, "the refused start reserved nothing");
    }

    [Fact]
    public async Task CreateConversation_ShouldReuseOwnConversation_WhenCallerAlreadyOwnsAnOpenOne()
    {
        var (user, agent) = _factory.NewAgent("starter");
        var (contactId, address) = await SeedWhatsAppContactAsync();
        var open = await SeedOpenConversationAsync(contactId, ConversationOwner.ForAgent(agent.AgentId));
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Hello!"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        response.Headers.Location!.ToString().Should().EndWith($"/conversations/{open.ConversationId.Value}");
        (await ConversationsOfAsync(contactId)).Should().ContainSingle("no second conversation is created");
        (await OutboundMessagesAsync(open.ConversationId)).Should().ContainSingle()
            .Which.DeliveryStatus.Should().Be(MessageDeliveryStatus.Sent);
        _factory.Graph.RequestsTo(address).Should().ContainSingle();
        _factory.LoadOf(agent.AgentId, ChannelType.WhatsApp).Should().Be(0, "a reuse adds no load");
    }

    [Fact]
    public async Task CreateConversation_ShouldStoreTheTemplateSent_WhenTemplateNamed()
    {
        var (user, _) = _factory.NewAgent("starter");
        var (contactId, _) = await SeedWhatsAppContactAsync();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Free text never sent"));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var conversationId = EntityId.From(response.Headers.Location!.ToString().Split('/')[^1]);
        var message = (await OutboundMessagesAsync(conversationId)).Should().ContainSingle().Subject;
        var text = message.Content.Blocks.OfType<TextBlock>().Should().ContainSingle().Subject.Text;
        text.Should().Be($"[template {Template} en_US]", "the transcript shows what the contact received");
    }

    [Theory]
    [InlineData("Pigeon")]
    [InlineData("99")]
    public async Task CreateConversation_ShouldReturn400UnknownChannel_WhenChannelIsNotDefined(string channel)
    {
        var (user, _) = _factory.NewAgent("starter");
        var (contactId, _) = await SeedWhatsAppContactAsync();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            "/api/v1/conversations", new { contactId = contactId.Value, channel, initialMessage = "Hello!" });

        var body = await ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, "unknown-channel");
        body.Should().NotContain(channel == "99" ? "\"99\"" : channel, "caller input is not echoed");
    }

    [Fact]
    public async Task CreateConversation_ShouldReturn409TemplateRequired_WhenNewWhatsAppStartHasNoTemplate()
    {
        var (user, agent) = _factory.NewAgent("starter");
        var (contactId, address) = await SeedWhatsAppContactAsync();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            "/api/v1/conversations", new { contactId = contactId.Value, channel = "WhatsApp", initialMessage = "Hi" });

        await ShouldBeErrorAsync(response, HttpStatusCode.Conflict, "whatsapp-template-required");
        (await OpenConversationAsync(contactId)).Should().BeNull();
        _factory.Graph.RequestsTo(address).Should().BeEmpty();
        _factory.LoadOf(agent.AgentId, ChannelType.WhatsApp).Should().Be(0);
    }

    [Fact]
    public async Task CreateConversation_ShouldReturn400NoAddress_WhenContactHasNoWhatsAppAddress()
    {
        var (user, _) = _factory.NewAgent("starter");
        await _factory.EnsureWhatsAppConfiguredAsync();
        var contact = new Contact
        {
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.Services.GetRequiredService<IContactStore>().SaveAsync(contact, CancellationToken.None);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contact.ContactId, initialMessage: "Hello!"));

        await ShouldBeErrorAsync(response, HttpStatusCode.BadRequest, "contact-has-no-address");
        (await OpenConversationAsync(contact.ContactId)).Should().BeNull();
    }

    [Fact]
    public async Task CreateConversation_ShouldReturn403_WhenCallerHasNoAgentProfile()
    {
        var user = _factory.NewUser(UserRole.Agent, "no-profile");
        var (contactId, address) = await SeedWhatsAppContactAsync();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Hello!"));

        await ShouldBeErrorAsync(response, HttpStatusCode.Forbidden, "not-an-agent");
        (await OpenConversationAsync(contactId)).Should().BeNull();
        _factory.Graph.RequestsTo(address).Should().BeEmpty();
    }

    // agent-console-contract-debt (D3, C7): what GET /channels/available lists is an offer, never a check on a start. The
    // refusal the console reports for a channel the tenant never configured stays the provider's, and comes after the send.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateConversation_ShouldGetPastTheRegistryCheck_WhenARegisteredChannelIsNotListed(bool hasInactiveRow)
    {
        await using var factory = new AgentStartApiFactory();
        var (user, _) = factory.NewAgent("starter");
        if (hasInactiveRow)
        {
            await factory.Services.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(
                new TenantChannelConfig
                {
                    TenantId = ConversationOwnershipApiFactory.Tenant,
                    Channel = ChannelType.WhatsApp,
                    IsActive = false,
                    Credentials = new Dictionary<string, string>(),
                },
                CancellationToken.None);
        }
        var (contactId, address) = await SeedContactAsync(factory, ChannelType.WhatsApp);
        using var client = factory.ClientFor(user);

        var listed = await client.GetAsync("/api/v1/channels/available");

        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await listed.Content.ReadAsStringAsync()).Should().NotContain("WhatsApp", "this start is on a channel the list leaves out");

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Hello!"));

        await ShouldBeErrorAsync(response, HttpStatusCode.BadGateway, "channel-send-failed");
        factory.Graph.RequestsTo(address).Should().BeEmpty("the connector has no credentials to send with");
    }

    [Fact]
    public async Task CreateConversation_ShouldReturn409NotAvailable_WhenTheTenantActivatedAChannelTheHostDoesNotRegister()
    {
        var (user, _) = _factory.NewAgent("starter");
        await _factory.Services.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(
            new TenantChannelConfig
            {
                TenantId = ConversationOwnershipApiFactory.Tenant,
                Channel = ChannelType.Sms,
                IsActive = true,
                Credentials = new Dictionary<string, string>(),
            },
            CancellationToken.None);
        var (contactId, _) = await SeedContactAsync(_factory, ChannelType.Sms);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(
            "/api/v1/conversations", new { contactId = contactId.Value, channel = "Sms", initialMessage = "Hello!" });

        var status = StartFixture()["refusals"]!.AsArray()
            .Single(refusal => refusal!["error"]!.GetValue<string>() == OutboundStartCodes.ChannelNotAvailable)!["status"]!;
        ((int)response.StatusCode).Should().Be(status.GetValue<int>());
        var body = await ShouldBeErrorAsync(response, response.StatusCode, OutboundStartCodes.ChannelNotAvailable);
        JsonNode.Parse(body)!.AsObject().Select(member => member.Key).Should().Equal(
            ["error"], "only the two refusals that name an open conversation add members");
    }

    [Fact]
    public async Task CreateConversation_ShouldStartFromTheFixturesMembers_WhenItsRequestIsPosted()
    {
        var fixture = StartFixture();
        var (user, _) = _factory.NewAgent("starter");
        var (contactId, _) = await SeedWhatsAppContactAsync();
        using var client = _factory.ClientFor(user);
        var request = fixture["request"]!.DeepClone().AsObject();
        request["contactId"] = contactId.Value;
        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");

        var response = await client.PostAsync(fixture["route"]!["path"]!.GetValue<string>(), content);

        var raw = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().Be(fixture["response"]!["status"]!.GetValue<int>(), raw);
        var conversationId = JsonNode.Parse(raw)!["conversationId"]!.GetValue<string>();
        response.Headers.Location!.ToString().Should().EndWith(conversationId);
        _factory.Load(EntityId.From(conversationId)).Should().NotBeNull();
    }

    [Fact]
    public async Task CreateConversation_ShouldNameTheOpenConversationAsTheFixtureDoes_WhenOpenElsewhere()
    {
        var fixture = StartFixture();
        var (user, _) = _factory.NewAgent("starter");
        var (_, otherAgent) = _factory.NewAgent("other");
        var (contactId, _) = await SeedWhatsAppContactAsync();
        await SeedOpenConversationAsync(contactId, ConversationOwner.ForAgent(otherAgent.AgentId));
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Hello!"));

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        var expected = fixture["refusalBody"]!.AsObject();
        body.Select(member => member.Key).Should().Equal(expected.Select(member => member.Key));
        body["error"]!.GetValue<string>().Should().Be(expected["error"]!.GetValue<string>());
        body["ownerKind"]!.GetValue<string>().Should().Be(expected["ownerKind"]!.GetValue<string>());
    }

    [Fact]
    public void OutboundStartCodes_ShouldBeTheFixturesRefusals_WithUnknownChannelBesideThem()
    {
        var refusals = StartFixture()["refusals"]!.AsArray().Select(refusal => refusal!["error"]!.GetValue<string>());

        refusals.Should().BeEquivalentTo(
            "unknown-channel",
            OutboundStartCodes.ContactHasNoAddress,
            OutboundStartCodes.ChannelNotAvailable,
            OutboundStartCodes.ConversationOpenElsewhere,
            OutboundStartCodes.ConversationNotSendable,
            OutboundStartCodes.WhatsAppTemplateRequired,
            OutboundStartCodes.AgentAtCapacity,
            OutboundStartCodes.StartPersistFailed,
            OutboundStartCodes.ChannelSendFailed);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static JsonObject StartFixture()
    {
        var file = Path.Join(AppContext.BaseDirectory, "Conversations", "Fixtures", "conversation-start.v1.json");
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    private static async Task<(EntityId ContactId, string Address)> SeedContactAsync(
        AgentStartApiFactory factory, ChannelType channel)
    {
        var contact = new Contact
        {
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var address = $"1555{Random.Shared.Next(1_000_000, 9_999_999)}";
        contact.AddAddress(new ChannelAddress(channel, address));
        await factory.Services.GetRequiredService<IContactStore>().SaveAsync(contact, CancellationToken.None);
        return (contact.ContactId, address);
    }

    private static object Body(EntityId contactId, string? initialMessage) => initialMessage is null
        ? new { contactId = contactId.Value, channel = "WhatsApp" }
        : new { contactId = contactId.Value, channel = "WhatsApp", initialMessage, templateName = Template, templateLanguage = "en_US" };

    private async Task<(EntityId ContactId, string Address)> SeedWhatsAppContactAsync()
    {
        await _factory.EnsureWhatsAppConfiguredAsync();
        var contact = new Contact
        {
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var address = $"1555{Random.Shared.Next(1_000_000, 9_999_999)}";
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, address));
        await _factory.Services.GetRequiredService<IContactStore>().SaveAsync(contact, CancellationToken.None);
        return (contact.ContactId, address);
    }

    private async Task<Conversation> SeedOpenConversationAsync(
        EntityId contactId, ConversationOwner? owner, ConversationState state = ConversationState.Active)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            ContactId = contactId,
            Channel = ChannelType.WhatsApp,
            State = state,
            Owner = owner,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.Services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        return conversation;
    }

    private Task<IReadOnlyList<Conversation>> ConversationsOfAsync(EntityId contactId) =>
        _factory.Services.GetRequiredService<IConversationStore>().ListByContactAsync(
            ConversationOwnershipApiFactory.Tenant, contactId, CancellationToken.None);

    private Task<Conversation?> OpenConversationAsync(EntityId contactId) =>
        _factory.Services.GetRequiredService<IConversationStore>().FindActiveByContactAsync(
            ConversationOwnershipApiFactory.Tenant, contactId, ChannelType.WhatsApp, CancellationToken.None);

    private async Task<IReadOnlyList<Message>> OutboundMessagesAsync(EntityId conversationId) =>
        (await _factory.Services.GetRequiredService<IMessageStore>().GetConversationMessagesAsync(
            ConversationOwnershipApiFactory.Tenant, conversationId, 100, 0, CancellationToken.None))
        .Where(m => m.Direction == MessageDirection.Outbound)
        .ToList();

    private static async Task<string> ShouldBeErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, because: $"the response was {body}");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("error").GetString().Should().Be(error);
        return body;
    }

    /// <summary>The ownership host plus a Graph API stub behind the WhatsApp connector's named client.</summary>
    public sealed class AgentStartApiFactory : ConversationOwnershipApiFactory
    {
        private int _configured;

        public GraphStub Graph { get; } = new();

        protected override void ConfigureTestServices(IServiceCollection services)
        {
            base.ConfigureTestServices(services);
            services.AddHttpClient(WhatsAppConnector.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Graph);
        }

        public async Task EnsureWhatsAppConfiguredAsync()
        {
            if (Interlocked.Exchange(ref _configured, 1) == 1)
                return;

            await Services.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(new TenantChannelConfig
            {
                TenantId = Tenant,
                Channel = ChannelType.WhatsApp,
                Credentials = new Dictionary<string, string>
                {
                    ["AccessToken"] = AccessToken,
                    ["PhoneNumberId"] = PhoneNumberId,
                    ["AppSecret"] = "agent-start-app-secret",
                    ["WebhookVerifyToken"] = "agent-start-verify",
                },
            }, CancellationToken.None);
        }
    }

    /// <summary>Answers each send with a fresh provider id, or with a Meta error for a rejected recipient; records requests by recipient.</summary>
    public sealed class GraphStub : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, bool> _rejected = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _bodies = new();

        public void Reject(string recipient) => _rejected[recipient] = true;

        public IReadOnlyList<string> RequestsTo(string recipient) =>
            _bodies.Where(b => b.Contains($"\"{recipient}\"", StringComparison.Ordinal)).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            _bodies.Enqueue(body);

            if (_rejected.Keys.Any(r => body.Contains($"\"{r}\"", StringComparison.Ordinal)))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"(#131026) Message undeliverable","type":"OAuthException","code":131026}}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"messaging_product":"whatsapp","messages":[{"id":"wamid.start-{{Guid.NewGuid():N}}"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
