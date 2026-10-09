using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
/// whatsapp-outbound and message-delivery-correlation through the real host: an agent reply on WhatsApp is
/// refused with <c>409 whatsapp-template-required</c> outside the 24-hour window (nothing reaches the
/// provider, nothing is stored), goes out as text inside it, and the provider id it is stamped with is what a
/// signed status webhook later correlates — forward only, so a replayed <c>sent</c> never undoes
/// <c>delivered</c>.
/// </summary>
public sealed class WhatsAppOutboundEndpointTests : IClassFixture<WhatsAppOutboundEndpointTests.WhatsAppOutboundApiFactory>
{
    private const string AppSecret = "wa-outbound-app-secret";
    private const string PhoneNumberId = "106540352242955";
    private const string AccessToken = "EAAoutbound";

    private readonly WhatsAppOutboundApiFactory _factory;

    public WhatsAppOutboundEndpointTests(WhatsAppOutboundApiFactory factory) => _factory = factory;

    [Fact]
    public async Task SendMessage_ShouldReturn409TemplateRequired_WhenWhatsAppWindowClosed()
    {
        var (user, conversation) = await SeedWhatsAppConversationAsync(lastInboundAgo: TimeSpan.FromHours(25));
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation), new { text = "are you still there?" });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        using (var json = JsonDocument.Parse(body))
            json.RootElement.GetProperty("error").GetString().Should().Be("whatsapp-template-required");
        _factory.Graph.RequestsFor(conversation.ConversationId).Should().BeEmpty("nothing reaches the provider");
        (await OutboundMessagesAsync(conversation)).Should().BeEmpty("a refused reply stores no message");
    }

    [Fact]
    public async Task SendMessage_ShouldReturn409TemplateRequired_WhenConversationHasNoInboundMessage()
    {
        var (user, conversation) = await SeedWhatsAppConversationAsync(lastInboundAgo: null);
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation), new { text = "hello" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.Graph.RequestsFor(conversation.ConversationId).Should().BeEmpty();
    }

    [Fact]
    public async Task SendMessage_ShouldSendText_WhenWindowOpen()
    {
        var (user, conversation) = await SeedWhatsAppConversationAsync(lastInboundAgo: TimeSpan.FromHours(1));
        using var client = _factory.ClientFor(user);

        var response = await client.PostAsJsonAsync(Route(conversation), new { text = "happy to help" });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var request = _factory.Graph.RequestsFor(conversation.ConversationId).Should().ContainSingle().Subject;
        request.Authorization.Should().Be($"Bearer {AccessToken}");
        request.Uri.Should().EndWith($"/{PhoneNumberId}/messages");
        request.Body.Should().Contain("\"type\":\"text\"").And.NotContain("template");
    }

    [Fact]
    public async Task Status_ShouldBecomeDelivered_WhenProviderPostsDeliveredForStampedId()
    {
        var (user, conversation) = await SeedWhatsAppConversationAsync(lastInboundAgo: TimeSpan.FromMinutes(5));
        var sent = await ReplyAsync(user, conversation);
        sent.DeliveryStatus.Should().Be(MessageDeliveryStatus.Sent);
        sent.ExternalMessageId.Should().NotBeNullOrEmpty("the provider id is stamped with Sent in one write");

        var webhook = await PostStatusAsync(sent.ExternalMessageId!, "delivered");

        webhook.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(sent.MessageId)).DeliveryStatus.Should().Be(MessageDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task Status_ShouldStayDelivered_WhenProviderReplaysSentAfterDelivered()
    {
        var (user, conversation) = await SeedWhatsAppConversationAsync(lastInboundAgo: TimeSpan.FromMinutes(5));
        var sent = await ReplyAsync(user, conversation);
        (await PostStatusAsync(sent.ExternalMessageId!, "delivered")).StatusCode.Should().Be(HttpStatusCode.OK);

        var replay = await PostStatusAsync(sent.ExternalMessageId!, "sent");

        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(sent.MessageId)).DeliveryStatus.Should().Be(MessageDeliveryStatus.Delivered);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string Route(Conversation conversation) =>
        $"/api/v1/conversations/{conversation.ConversationId.Value}/messages";

    private async Task<(Verbara.Platform.Identity.User User, Conversation Conversation)> SeedWhatsAppConversationAsync(
        TimeSpan? lastInboundAgo)
    {
        await _factory.EnsureWhatsAppConfiguredAsync();
        var (user, agent) = _factory.NewAgent("wa-outbound");
        var services = _factory.Services;
        var contact = new Contact
        {
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var address = $"1555{Random.Shared.Next(1_000_000, 9_999_999)}";
        contact.AddAddress(new ChannelAddress(ChannelType.WhatsApp, address));
        await services.GetRequiredService<IContactStore>().SaveAsync(contact, CancellationToken.None);

        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            ContactId = contact.ContactId,
            Channel = ChannelType.WhatsApp,
            State = ConversationState.Active,
            Owner = ConversationOwner.ForAgent(agent.AgentId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        _factory.Graph.Map(address, conversation.ConversationId);

        if (lastInboundAgo is { } ago)
        {
            var at = DateTimeOffset.UtcNow - ago;
            await services.GetRequiredService<IMessageStore>().SaveAsync(new Message
            {
                MessageId = EntityId.New(),
                ConversationId = conversation.ConversationId,
                TenantId = ConversationOwnershipApiFactory.Tenant,
                Direction = MessageDirection.Inbound,
                Channel = ChannelType.WhatsApp,
                SenderId = address,
                Content = new MessageEnvelope([new TextBlock("hola")]),
                DeliveryStatus = MessageDeliveryStatus.Delivered,
                ExternalMessageId = $"wamid.in-{Guid.NewGuid():N}",
                CreatedAt = at,
                DeliveredAt = at,
            }, CancellationToken.None);
        }

        return (user, conversation);
    }

    private async Task<Message> ReplyAsync(Verbara.Platform.Identity.User user, Conversation conversation)
    {
        using var client = _factory.ClientFor(user);
        var response = await client.PostAsJsonAsync(Route(conversation), new { text = "on its way" });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await OutboundMessagesAsync(conversation)).Should().ContainSingle().Subject;
    }

    private async Task<IReadOnlyList<Message>> OutboundMessagesAsync(Conversation conversation) =>
        (await _factory.Services.GetRequiredService<IMessageStore>().GetConversationMessagesAsync(
            ConversationOwnershipApiFactory.Tenant, conversation.ConversationId, 100, 0, CancellationToken.None))
        .Where(m => m.Direction == MessageDirection.Outbound)
        .ToList();

    private async Task<Message> LoadAsync(EntityId messageId) =>
        (await _factory.Services.GetRequiredService<IMessageStore>().GetByIdAsync(
            ConversationOwnershipApiFactory.Tenant, messageId, CancellationToken.None))!;

    private async Task<HttpResponseMessage> PostStatusAsync(string externalId, string status)
    {
        var body = Encoding.UTF8.GetBytes($$"""
            {
              "object": "whatsapp_business_account",
              "entry": [{ "id": "WABA", "changes": [{ "field": "messages", "value": {
                "messaging_product": "whatsapp",
                "metadata": { "display_phone_number": "15550000001", "phone_number_id": "{{PhoneNumberId}}" },
                "statuses": [{ "id": "{{externalId}}", "status": "{{status}}", "timestamp": "1700000100", "recipient_id": "15550000002" }]
              } }] }]
            }
            """);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.Add(
            "X-Hub-Signature-256",
            "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), body)).ToLowerInvariant());
        using var client = _factory.CreateClient();
        return await client.PostAsync($"/api/v1/webhooks/{ConversationOwnershipApiFactory.Tenant.Value}/whatsapp", content);
    }

    /// <summary>The ownership host plus a Graph API stub behind the WhatsApp connector's named client.</summary>
    public sealed class WhatsAppOutboundApiFactory : ConversationOwnershipApiFactory
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
                    ["AppSecret"] = AppSecret,
                    ["WebhookVerifyToken"] = "wa-outbound-verify",
                },
            }, CancellationToken.None);
        }
    }

    /// <summary>Answers every send with a fresh provider id and records it per conversation (by recipient).</summary>
    public sealed class GraphStub : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, EntityId> _conversationByRecipient = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<(EntityId? Conversation, GraphRequest Request)> _requests = new();

        public void Map(string recipient, EntityId conversationId) => _conversationByRecipient[recipient] = conversationId;

        public IReadOnlyList<GraphRequest> RequestsFor(EntityId conversationId) =>
            _requests.Where(r => r.Conversation == conversationId).Select(r => r.Request).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var recipient = _conversationByRecipient.Keys.FirstOrDefault(r => body.Contains($"\"{r}\"", StringComparison.Ordinal));
            _requests.Enqueue((
                recipient is null ? null : _conversationByRecipient[recipient],
                new GraphRequest(request.RequestUri?.ToString() ?? string.Empty, request.Headers.Authorization?.ToString(), body)));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"messaging_product":"whatsapp","messages":[{"id":"wamid.out-{{Guid.NewGuid():N}}"}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    public sealed record GraphRequest(string Uri, string? Authorization, string Body);
}
