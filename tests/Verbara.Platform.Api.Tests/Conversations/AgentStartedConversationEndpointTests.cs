using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WhatsApp;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// agent-started-conversations (design D14) through the real host: <c>POST /conversations</c> with an
/// <c>initialMessage</c> sends first and persists nothing when the provider rejects the send
/// (<c>502 channel-send-failed</c>); a success is born <c>Active</c> and owned by the caller's Agent profile
/// with the message stored <c>Sent</c>; an open conversation owned by another agent is refused with
/// <c>409 conversation-open-elsewhere</c>. Without an initial message the deprecated queued path stays.
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
        var (user, _) = _factory.NewAgent("starter");
        var (contactId, address) = await SeedWhatsAppContactAsync();
        _factory.Graph.Reject(address);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: "Hello!"));

        // The orphan is checked first: it is the v2.27.0 residual this block closes.
        (await OpenConversationAsync(contactId)).Should().BeNull("a failed first send persists no conversation");
        await ShouldBeErrorAsync(response, HttpStatusCode.BadGateway, "channel-send-failed");
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
            json.RootElement.GetProperty("conversationId").GetString().Should().Be(open.ConversationId.Value);
        _factory.Load(open.ConversationId)!.Owner.Should().Be(ConversationOwner.ForAgent(otherAgent.AgentId));
        _factory.Graph.RequestsTo(address).Should().BeEmpty("nothing is sent");
    }

    [Fact]
    public async Task CreateConversation_ShouldReturn201Queued_WhenNoInitialMessage()
    {
        var (user, _) = _factory.NewAgent("starter");
        var (contactId, _) = await SeedWhatsAppContactAsync();
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync("/api/v1/conversations", Body(contactId, initialMessage: null));

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var stored = (await OpenConversationAsync(contactId))!;
        stored.State.Should().Be(ConversationState.Queued);
        stored.Owner.Should().BeNull();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

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

    private async Task<Conversation> SeedOpenConversationAsync(EntityId contactId, ConversationOwner owner)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            ContactId = contactId,
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Active,
            Owner = owner,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _factory.Services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        return conversation;
    }

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
