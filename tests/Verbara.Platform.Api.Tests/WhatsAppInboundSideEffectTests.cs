using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Verbara.Platform.Bot;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Switchboard;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// whatsapp-works-for-real — what an accepted WhatsApp delivery does after it is stored, through the real
/// host: a replayed message id has no side effect at all (no event, no routing, no queue assignment, no bot
/// turn); one event of a batch whose routing fails never fails the request or the rest of the batch; and a
/// customer's follow-up on a conversation that is already offered, active or on hold is not routed again.
/// </summary>
public sealed class WhatsAppInboundSideEffectTests : IClassFixture<PlatformApiFactory>
{
    private const string AppSecret = "wa-side-effect-secret";
    private const string PhoneNumberId = "106540352242977";
    private const string FailingSender = "15550000012";

    private readonly PlatformApiFactory _factory;

    public WhatsAppInboundSideEffectTests(PlatformApiFactory factory) => _factory = factory;

    // ── B1: replays are idempotent end to end ─────────────────────────────────

    [Fact]
    public async Task PostWebhook_ShouldHaveNoSideEffect_WhenSameMessageIdIsDeliveredTwice()
    {
        await using var host = await SideEffectHost.StartAsync(_factory);
        var body = Payload([TextMessage("15550000002", "wamid.replay1", "hola")]);

        (await host.PostAsync(body)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.PostAsync(body)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await host.CountRowsAsync()).Should().Be((1, 1, 1));
        host.MessageEvents.Should().Be(1, "a replay publishes no second message event");
        host.Router.Calls.Should().Be(1, "a replay is not routed again");
        host.Switchboard.AssignToQueueCalls.Should().Be(1, "a replay is not queued again");
    }

    [Fact]
    public async Task PostWebhook_ShouldRunBotOnce_WhenFollowUpOnBotOwnedConversationIsReplayed()
    {
        await using var host = await SideEffectHost.StartAsync(_factory);
        (await host.PostAsync(Payload([TextMessage("15550000002", "wamid.bot1", "hola")]))).StatusCode
            .Should().Be(HttpStatusCode.OK);
        var conversation = await host.SingleConversationAsync();
        conversation.Owner = ConversationOwner.ForBot(EntityId.From("bot-side-effect"));
        await host.SaveAsync(conversation);

        var followUp = Payload([TextMessage("15550000002", "wamid.bot2", "quiero ayuda")]);
        (await host.PostAsync(followUp)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.PostAsync(followUp)).StatusCode.Should().Be(HttpStatusCode.OK);

        await host.Bot.Received(1).ProcessMessageAsync(
            conversation.ConversationId, host.Tenant, Arg.Any<MessageEnvelope>(), Arg.Any<CancellationToken>());
        host.Router.Calls.Should().Be(1, "only the message that opened the conversation was routed");
        host.MessageEvents.Should().Be(2);
        (await host.CountRowsAsync()).Should().Be((1, 1, 2));
    }

    // ── B2: one event's routing failure does not fail the batch ───────────────

    [Fact]
    public async Task PostWebhook_ShouldPersistEveryEventAndReturn200_WhenOneMessageFailsRouting()
    {
        await using var host = await SideEffectHost.StartAsync(_factory, failRoutingFor: FailingSender);
        var outbound = await host.SeedOutboundAsync("wamid.out-b2");
        var body = Payload(
            [
                TextMessage("15550000011", "wamid.b2-1", "uno"),
                TextMessage(FailingSender, "wamid.b2-2", "dos"),
                TextMessage("15550000013", "wamid.b2-3", "tres"),
            ],
            [DeliveredStatus("wamid.out-b2")]);

        var first = await host.PostAsync(body);

        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        (await host.CountRowsAsync()).Should().Be((3, 3, 3), "every inbound message of the batch is stored");
        (await host.MessageAsync(outbound)).DeliveryStatus.Should().Be(MessageDeliveryStatus.Delivered);
        var conversations = await host.ConversationsAsync();
        conversations.Count(c => c.Owner?.Kind == ConversationOwnerKind.Queue).Should().Be(2);
        conversations.Should().ContainSingle(c => c.Owner == null && c.State == ConversationState.Queued,
            "the conversation whose routing failed is left in its pre-routing state");
        host.Router.Calls.Should().Be(3);

        var replay = await host.PostAsync(body);

        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.CountRowsAsync()).Should().Be((3, 3, 3), "a replay stores no duplicate");
        host.Router.Calls.Should().Be(3, "a replay has no side effect");
        host.Switchboard.AssignToQueueCalls.Should().Be(2);
        host.MessageEvents.Should().Be(3);
    }

    // ── B3: a follow-up does not re-route an offered, active or held conversation ──

    [Theory]
    [InlineData(ConversationState.Offered)]
    [InlineData(ConversationState.Active)]
    [InlineData(ConversationState.OnHold)]
    public async Task PostWebhook_ShouldNotRouteAgain_WhenFollowUpArrivesOnAssignedConversation(ConversationState state)
    {
        await using var host = await SideEffectHost.StartAsync(_factory);
        (await host.PostAsync(Payload([TextMessage("15550000002", "wamid.f1", "hola")]))).StatusCode
            .Should().Be(HttpStatusCode.OK);
        var conversation = await host.SingleConversationAsync();
        conversation.TransitionTo(ConversationState.Offered, DateTimeOffset.UtcNow);
        if (state is ConversationState.Active or ConversationState.OnHold)
        {
            conversation.TransitionTo(ConversationState.Active, DateTimeOffset.UtcNow);
            conversation.Owner = ConversationOwner.ForAgent(EntityId.From("agent-side-effect"));
        }

        if (state == ConversationState.OnHold)
            conversation.TransitionTo(ConversationState.OnHold, DateTimeOffset.UtcNow);

        await host.SaveAsync(conversation);
        var ownerBefore = conversation.Owner;

        (await host.PostAsync(Payload([TextMessage("15550000002", "wamid.f2", "sigo aqui")]))).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var after = await host.SingleConversationAsync();
        after.State.Should().Be(state);
        after.Owner.Should().Be(ownerBefore);
        host.Router.Calls.Should().Be(1);
        host.Switchboard.AssignToQueueCalls.Should().Be(1, "only the first message queued the conversation");
        host.MessageEvents.Should().Be(2, "the follow-up is still announced to the agent UI");
    }

    // ── payloads ──────────────────────────────────────────────────────────────

    private static string TextMessage(string from, string id, string text) => $$"""
        { "from": "{{from}}", "id": "{{id}}", "timestamp": "1700000000", "type": "text", "text": { "body": "{{text}}" } }
        """;

    private static string DeliveredStatus(string id) => $$"""
        { "id": "{{id}}", "status": "delivered", "timestamp": "1700000100", "recipient_id": "15550000002" }
        """;

    private static byte[] Payload(string[] messages, string[]? statuses = null) =>
        Encoding.UTF8.GetBytes($$"""
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "WABA",
                "changes": [{
                  "field": "messages",
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": { "display_phone_number": "15550000001", "phone_number_id": "{{PhoneNumberId}}" },
                    "messages": [{{string.Join(",", messages)}}],
                    "statuses": [{{string.Join(",", statuses ?? [])}}]
                  }
                }]
              }]
            }
            """);

    /// <summary>A host with recording decorators over the real router and switchboard and a substitute bot.</summary>
    private sealed class SideEffectHost : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _host;
        private readonly HttpClient _client;
        private readonly ConcurrentBag<ConversationMessageEvent> _events = [];
        private readonly IDisposable _subscription;

        private SideEffectHost(WebApplicationFactory<Program> host, RecordingRouter router, RecordingSwitchboard switchboard, IVirtualAgent bot)
        {
            _host = host;
            _client = host.CreateClient();
            Router = router;
            Switchboard = switchboard;
            Bot = bot;
            _subscription = host.Services.GetRequiredService<PlatformEventBus>().Events
                .Subscribe(new EventObserver(this));
        }

        public TenantId Tenant { get; } = new($"wase-{Guid.NewGuid():N}"[..20]);

        public RecordingRouter Router { get; }

        public RecordingSwitchboard Switchboard { get; }

        public IVirtualAgent Bot { get; }

        public int MessageEvents => _events.Count(e => e.TenantId == Tenant.Value);

        public static async Task<SideEffectHost> StartAsync(PlatformApiFactory factory, string? failRoutingFor = null)
        {
            RecordingRouter? router = null;
            RecordingSwitchboard? switchboard = null;
            var bot = Substitute.For<IVirtualAgent>();
            bot.ProcessMessageAsync(default, default, default!, default)
                .ReturnsForAnyArgs(new BotResponse(BotResponseAction.Reply, null, null, null, null));

            var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInboundRouter>();
                services.AddSingleton<IInboundRouter>(sp =>
                    router = new RecordingRouter(ActivatorUtilities.CreateInstance<InboundRouter>(sp), failRoutingFor));
                services.RemoveAll<IConversationSwitchboard>();
                services.AddSingleton<IConversationSwitchboard>(sp =>
                    switchboard = new RecordingSwitchboard(ActivatorUtilities.CreateInstance<ConversationSwitchboard>(sp)));
                services.RemoveAll<IVirtualAgent>();
                services.AddSingleton(bot);
            }));

            // Resolve both decorators now so the counters exist before the first request.
            _ = host.Services.GetRequiredService<IInboundRouter>();
            _ = host.Services.GetRequiredService<IConversationSwitchboard>();
            var started = new SideEffectHost(host, router!, switchboard!, bot);
            await WebhookProbes.ConfigureAsync(host.Services, started.Tenant, ChannelType.WhatsApp, new Dictionary<string, string>
            {
                ["AccessToken"] = "EAAside",
                ["PhoneNumberId"] = PhoneNumberId,
                ["AppSecret"] = AppSecret,
                ["WebhookVerifyToken"] = "side-verify",
            });
            return started;
        }

        public async Task<HttpResponseMessage> PostAsync(byte[] body)
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), body);
            content.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(hash).ToLowerInvariant());
            return await _client.PostAsync($"/api/v1/webhooks/{Tenant.Value}/whatsapp", content);
        }

        public Task<(int Contacts, int Conversations, int Messages)> CountRowsAsync() =>
            WebhookProbes.CountRowsAsync(_host.Services, Tenant);

        public async Task<IReadOnlyList<Conversation>> ConversationsAsync() =>
            (await _host.Services.GetRequiredService<IConversationStore>()
                .ListAsync(Tenant, new ConversationQuery { PageSize = 100 }, CancellationToken.None)).Items;

        public async Task<Conversation> SingleConversationAsync() => (await ConversationsAsync()).Should().ContainSingle().Subject;

        public Task SaveAsync(Conversation conversation) =>
            _host.Services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);

        public async Task<EntityId> SeedOutboundAsync(string externalId)
        {
            var message = new Message
            {
                MessageId = EntityId.New(),
                ConversationId = EntityId.New(),
                TenantId = Tenant,
                Direction = MessageDirection.Outbound,
                Channel = ChannelType.WhatsApp,
                SenderId = "agent-side-effect",
                Content = new MessageEnvelope([new TextBlock("hola desde el agente")]),
                DeliveryStatus = MessageDeliveryStatus.Sent,
                ExternalMessageId = externalId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await _host.Services.GetRequiredService<IMessageStore>().SaveAsync(message, CancellationToken.None);
            return message.MessageId;
        }

        public async Task<Message> MessageAsync(EntityId messageId) =>
            (await _host.Services.GetRequiredService<IMessageStore>().GetByIdAsync(Tenant, messageId, CancellationToken.None))!;

        public async ValueTask DisposeAsync()
        {
            _subscription.Dispose();
            _client.Dispose();
            await _host.DisposeAsync();
        }

        private sealed class EventObserver(SideEffectHost owner) : IObserver<PlatformEvent>
        {
            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
            }

            public void OnNext(PlatformEvent value)
            {
                if (value is ConversationMessageEvent message)
                    owner._events.Add(message);
            }
        }
    }

    /// <summary>The real router, counting calls; throws the router's own no-route failure for one sender.</summary>
    private sealed class RecordingRouter(IInboundRouter inner, string? failFor) : IInboundRouter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<RouteResult> RouteAsync(RoutingContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (failFor is not null && context.Contact.FindAddress(ChannelType.WhatsApp)?.Address == failFor)
                throw new InvalidOperationException("Routing pipeline produced no result.");
            return inner.RouteAsync(context, ct);
        }
    }

    /// <summary>The real switchboard, counting queue assignments.</summary>
    private sealed class RecordingSwitchboard(IConversationSwitchboard inner) : IConversationSwitchboard
    {
        private int _assignToQueueCalls;

        public int AssignToQueueCalls => Volatile.Read(ref _assignToQueueCalls);

        public Task<OwnershipResult> AssignToQueueAsync(EntityId conversationId, TenantId tenantId, EntityId queueId, CancellationToken ct)
        {
            Interlocked.Increment(ref _assignToQueueCalls);
            return inner.AssignToQueueAsync(conversationId, tenantId, queueId, ct);
        }

        public Task<OwnershipResult> OfferToAgentAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct) =>
            inner.OfferToAgentAsync(conversationId, tenantId, agentId, ct);

        public Task<OwnershipResult> AcceptAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct) =>
            inner.AcceptAsync(conversationId, tenantId, agentId, ct);

        public Task<OwnershipResult> RejectAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct) =>
            inner.RejectAsync(conversationId, tenantId, agentId, ct);

        public Task<OwnershipResult> TransferToQueueAsync(EntityId conversationId, TenantId tenantId, EntityId targetQueueId, CancellationToken ct) =>
            inner.TransferToQueueAsync(conversationId, tenantId, targetQueueId, ct);

        public Task<OwnershipResult> RequeueToFrontAsync(EntityId conversationId, TenantId tenantId, EntityId targetQueueId, CancellationToken ct) =>
            inner.RequeueToFrontAsync(conversationId, tenantId, targetQueueId, ct);

        public Task<OwnershipResult> TransferToAgentAsync(
            EntityId conversationId, TenantId tenantId, EntityId targetAgentId, OwnershipChange change, CancellationToken ct) =>
            inner.TransferToAgentAsync(conversationId, tenantId, targetAgentId, change, ct);

        public Task<OwnershipResult> ReturnToBotAsync(EntityId conversationId, TenantId tenantId, EntityId botId, CancellationToken ct) =>
            inner.ReturnToBotAsync(conversationId, tenantId, botId, ct);

        public Task<OwnershipResult> HoldAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct) =>
            inner.HoldAsync(conversationId, tenantId, agentId, ct);

        public Task<OwnershipResult> UnholdAsync(EntityId conversationId, TenantId tenantId, EntityId agentId, CancellationToken ct) =>
            inner.UnholdAsync(conversationId, tenantId, agentId, ct);
    }
}
