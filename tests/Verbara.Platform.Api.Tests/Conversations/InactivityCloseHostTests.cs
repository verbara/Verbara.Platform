using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WebChat;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Surveys;
using Verbara.Platform.Switchboard;
using Verbara.Sdk.Pro.CsatRunner.Contracts;

namespace Verbara.Platform.Api.Tests.Conversations;

/// <summary>
/// conversations-close-after-inactivity task 3.1: the inactivity close through the real host (the real
/// <c>Program.cs</c> graph, in-memory storage, a fake clock). A WebChat conversation an agent accepted and nobody wrote
/// to for 72 h 30 min is closed by the timeout worker with reason <c>inactivity</c>, frees the agent's unit, counts on
/// <c>conversations.system_closed</c> and pushes no CSAT signal, while a control conversation on the same survey and
/// queue closed by its agent pushes exactly one (the CSAT half runs on WebChat because
/// <see cref="CsatConversationEndSource"/> returns before any reason check for WhatsApp, audit round 3 F2). The
/// visitor's next message opens a new, routed conversation. A WhatsApp variant runs the webhook path.
/// </summary>
public sealed class InactivityCloseHostTests : IDisposable
{
    private const string AppSecret = "inactivity-host-secret";
    private const string PhoneNumberId = "106540352242999";
    private static readonly TenantId Tenant = new(AuthenticatedPlatformApiFactory.TestTenantId);
    private static readonly TimeSpan IdleFor = TimeSpan.FromHours(72) + TimeSpan.FromMinutes(30);

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>The authenticated host with its <see cref="IClock"/> replaced by a clock the test advances.</summary>
    public sealed class Factory : AuthenticatedPlatformApiFactory
    {
        public FakeClock Clock { get; } = new(DateTimeOffset.UtcNow);

        protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
        {
            builder.ConfigureServices((_, services) =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(Clock);
            });
            return base.CreateHost(builder);
        }
    }

    /// <summary>A settable clock: every <see cref="IClock"/> reader of the host sees the same time.</summary>
    public sealed class FakeClock(DateTimeOffset start) : IClock
    {
        private long _ticks = start.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    [Fact]
    public async Task TimeoutWorker_ShouldCloseSilentlyWithoutCsat_WhenAnAcceptedWebChatConversationIsIdlePastTheWindow()
    {
        var services = _factory.Services;
        AuthenticatedPlatformApiFactory.SeedWebChatChannel(services, Tenant.Value);
        var queue = await SeedCsatQueueAsync(services);
        await SeedCsatSurveyAsync(services);
        var agent = await SeedAgentAsync(services);

        var endSource = services.GetRequiredService<CsatConversationEndSource>();
        await endSource.StartAsync(CancellationToken.None);
        // ExecuteAsync runs off the start call (.NET 10 BackgroundService): wait for its subscription.
        (await WaitUntilAsync(() => endSource.IsSubscribed)).Should().BeTrue();
        var signals = new List<CsatConversationEndedSignal>();
        using var signalSubscription = endSource.Ended.Subscribe(s => { lock (signals) signals.Add(s); });
        using var counter = new SystemClosedCounter(services.GetRequiredService<ConversationCloseMetrics>());

        try
        {
            using var client = _factory.CreateAuthenticatedClient();

            // 1. A visitor message opens X; it routes to the CSAT queue and the agent accepts it.
            var sessionId = await OpenSessionAsync(client);
            await SendVisitorMessageAsync(client, sessionId, "hola");
            var x = await SessionConversationAsync(services, sessionId);
            await AcceptAsync(services, x, queue.QueueId, agent.AgentId);
            LoadOf(services, agent.AgentId).Should().Be(1);

            // 2. 72 h 30 min pass and the timeout worker ticks.
            _factory.Clock.Advance(IdleFor);
            await CreateWorker(services).ProcessTimeoutsAsync(CancellationToken.None);

            // 3. X is closed for inactivity, the agent's unit is back, the counter grew by one.
            var closedX = await LoadAsync(services, x);
            closedX.State.Should().Be(ConversationState.Closed);
            closedX.Metadata[ConversationCloseReasons.MetadataKey].Should().Be(ConversationCloseReasons.Inactivity);
            LoadOf(services, agent.AgentId).Should().Be(0);
            counter.Count(ConversationCloseReasons.Inactivity).Should().Be(1);

            // 5. The visitor writes again from the same session: a second conversation, assigned to a queue.
            await SendVisitorMessageAsync(client, sessionId, "sigo aquí");
            var next = await SessionConversationAsync(services, sessionId);
            next.Should().NotBe(x);
            var nextRow = await LoadAsync(services, next);
            nextRow.State.Should().Be(ConversationState.Queued);
            nextRow.Owner.Should().Be(ConversationOwner.ForQueue(queue.QueueId));

            // 6. Control: Y on the same survey and queue, accepted and closed by its agent's normal close.
            var controlSession = await OpenSessionAsync(client);
            await SendVisitorMessageAsync(client, controlSession, "otra consulta");
            var y = await SessionConversationAsync(services, controlSession);
            await AcceptAsync(services, y, queue.QueueId, agent.AgentId);
            using (var close = await client.PostAsync($"/api/v1/conversations/{y.Value}/close", null))
                close.StatusCode.Should().Be(HttpStatusCode.OK, await close.Content.ReadAsStringAsync());
            (await LoadAsync(services, y)).Metadata.Should().NotContainKey(ConversationCloseReasons.MetadataKey);

            (await WaitUntilAsync(() => SignalsFor(signals, y) == 1)).Should().BeTrue(
                "the control's agent close pushes exactly one CSAT signal; without it a green step 4 proves nothing");

            // 4. Asserted after the control's signal arrived: X's event was handled at least as long ago.
            SignalsFor(signals, x).Should().Be(0, "a system close never solicits CSAT (D6)");
            SignalsFor(signals, y).Should().Be(1);
        }
        finally
        {
            await endSource.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TimeoutWorker_ShouldCloseAndFreeTheAgent_WhenAnAcceptedWhatsAppConversationIsIdlePastTheWindow()
    {
        var services = _factory.Services;
        await services.GetRequiredService<ITenantChannelConfigStore>().SaveAsync(new TenantChannelConfig
        {
            TenantId = Tenant,
            Channel = ChannelType.WhatsApp,
            Credentials = new Dictionary<string, string>
            {
                ["AccessToken"] = "EAAinactivity",
                ["PhoneNumberId"] = PhoneNumberId,
                ["AppSecret"] = AppSecret,
                ["WebhookVerifyToken"] = "inactivity-verify",
            },
        }, CancellationToken.None);
        var queue = await SeedCsatQueueAsync(services);
        var agent = await SeedAgentAsync(services);
        using var counter = new SystemClosedCounter(services.GetRequiredService<ConversationCloseMetrics>());
        using var client = _factory.CreateClient();

        // 1. A customer message opens X through the webhook; it routes to the queue and the agent accepts it.
        using (var first = await PostWhatsAppAsync(client, "wamid.idle1", "hola"))
            first.StatusCode.Should().Be(HttpStatusCode.OK);
        var x = (await ConversationsAsync(services)).Should().ContainSingle().Subject;
        x.Owner.Should().Be(ConversationOwner.ForQueue(queue.QueueId));
        await AcceptAsync(services, x.ConversationId, queue.QueueId, agent.AgentId);
        LoadOf(services, agent.AgentId, ChannelType.WhatsApp).Should().Be(1);

        // 2-3. 72 h 30 min later the worker closes X, frees the agent and counts the close.
        _factory.Clock.Advance(IdleFor);
        await CreateWorker(services).ProcessTimeoutsAsync(CancellationToken.None);

        var closedX = await LoadAsync(services, x.ConversationId);
        closedX.State.Should().Be(ConversationState.Closed);
        closedX.Metadata[ConversationCloseReasons.MetadataKey].Should().Be(ConversationCloseReasons.Inactivity);
        LoadOf(services, agent.AgentId, ChannelType.WhatsApp).Should().Be(0);
        counter.Count(ConversationCloseReasons.Inactivity).Should().Be(1);

        // 5. The customer writes again: a new conversation, routed to the queue.
        using (var second = await PostWhatsAppAsync(client, "wamid.idle2", "sigo aquí"))
            second.StatusCode.Should().Be(HttpStatusCode.OK);
        var next = (await ConversationsAsync(services)).Should().ContainSingle(c => c.ConversationId != x.ConversationId).Subject;
        next.State.Should().Be(ConversationState.Queued);
        next.Owner.Should().Be(ConversationOwner.ForQueue(queue.QueueId));
    }

    // ─── Seeding ──────────────────────────────────────────────────────────────

    private static async Task<Queue> SeedCsatQueueAsync(IServiceProvider services)
    {
        var queue = new Queue
        {
            QueueId = EntityId.From($"queue-csat-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            Name = "Inactivity CSAT queue",
            IsActive = true,
            Csat = new CsatConfig(Enabled: true, PreferredChannel: "webchat", PromptTemplateId: null, SamplingRatePercent: 100),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await services.GetRequiredService<IQueueStore>().SaveAsync(queue, CancellationToken.None);
        return queue;
    }

    private static Task SeedCsatSurveyAsync(IServiceProvider services) =>
        services.GetRequiredService<ISurveyStore>().SaveAsync(new Survey
        {
            SurveyId = EntityId.From($"survey-csat-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            Name = "Customer Satisfaction",
            Type = SurveyType.Csat,
            Questions = [],
        }, CancellationToken.None);

    // The agent profile of the host's admin user, so the authenticated client is the conversation's owner and the
    // account-status check at offer and accept reads an Active user.
    private static async Task<Agent> SeedAgentAsync(IServiceProvider services)
    {
        var agent = new Agent
        {
            AgentId = EntityId.From($"agent-idle-{Guid.NewGuid():N}"),
            TenantId = Tenant,
            UserId = EntityId.From(AuthenticatedPlatformApiFactory.TestUserId),
            DisplayName = "Idle agent",
            State = AgentState.Available,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await services.GetRequiredService<IAgentStore>().SaveAsync(agent, CancellationToken.None);
        return agent;
    }

    // ─── Driving ──────────────────────────────────────────────────────────────

    private static async Task<string> OpenSessionAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/webchat/sessions", new { tenantId = Tenant.Value });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("sessionId").GetString()!;
    }

    private static async Task SendVisitorMessageAsync(HttpClient client, string sessionId, string text)
    {
        using var send = await client.PostAsJsonAsync($"/api/v1/webchat/sessions/{sessionId}/messages", new { text });
        send.StatusCode.Should().Be(HttpStatusCode.OK, await send.Content.ReadAsStringAsync());
    }

    private static async Task<EntityId> SessionConversationAsync(IServiceProvider services, string sessionId) =>
        (await services.GetRequiredService<WebChatSessionManager>().GetSessionAsync(sessionId))!.ConversationId;

    private static async Task AcceptAsync(IServiceProvider services, EntityId conversationId, EntityId queueId, EntityId agentId)
    {
        var row = await LoadAsync(services, conversationId);
        row.State.Should().Be(ConversationState.Queued);
        row.Owner.Should().Be(ConversationOwner.ForQueue(queueId), "the conversation routed to the CSAT queue");

        var switchboard = services.GetRequiredService<IConversationSwitchboard>();
        var offer = await switchboard.OfferToAgentAsync(conversationId, Tenant, agentId, CancellationToken.None);
        offer.Success.Should().BeTrue(offer.FailureReason);
        var accept = await switchboard.AcceptAsync(conversationId, Tenant, agentId, CancellationToken.None);
        accept.Success.Should().BeTrue(accept.FailureReason);
    }

    private async Task<HttpResponseMessage> PostWhatsAppAsync(HttpClient client, string messageId, string text)
    {
        var timestamp = _factory.Clock.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var body = Encoding.UTF8.GetBytes($$"""
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "WABA",
                "changes": [{
                  "field": "messages",
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": { "display_phone_number": "15550000001", "phone_number_id": "{{PhoneNumberId}}" },
                    "messages": [{ "from": "15550000077", "id": "{{messageId}}", "timestamp": "{{timestamp}}", "type": "text", "text": { "body": "{{text}}" } }]
                  }
                }]
              }]
            }
            """);
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppSecret), body);
        content.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(hash).ToLowerInvariant());
        return await client.PostAsync($"/api/v1/webhooks/{Tenant.Value}/whatsapp", content);
    }

    // The host composes the worker as a hosted service, which the test host strips; build the same singleton graph.
    private static ConversationTimeoutWorker CreateWorker(IServiceProvider services) =>
        ActivatorUtilities.CreateInstance<ConversationTimeoutWorker>(services);

    // ─── Reading ──────────────────────────────────────────────────────────────

    private static async Task<Conversation> LoadAsync(IServiceProvider services, EntityId conversationId) =>
        (await services.GetRequiredService<IConversationStore>().GetByIdAsync(Tenant, conversationId, CancellationToken.None))!;

    private static async Task<IReadOnlyList<Conversation>> ConversationsAsync(IServiceProvider services) =>
        (await services.GetRequiredService<IConversationStore>()
            .ListAsync(Tenant, new ConversationQuery { Channel = ChannelType.WhatsApp, PageSize = 100 }, CancellationToken.None)).Items;

    private static int LoadOf(IServiceProvider services, EntityId agentId, ChannelType channel = ChannelType.WebChat) =>
        services.GetRequiredService<IAgentCapacityService>()
            .GetCurrentLoadAsync(Tenant, agentId, channel, CancellationToken.None).GetAwaiter().GetResult();

    private static int SignalsFor(List<CsatConversationEndedSignal> signals, EntityId conversationId)
    {
        lock (signals)
            return signals.Count(s => s.ConversationId == conversationId.Value);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
                return true;
            await Task.Delay(20); // fence-allow: LOOP-DRIVER — poll pacing for the CSAT source's fire-and-forget handler
        }
        return predicate();
    }

    /// <summary>Sums <c>conversations.system_closed</c> of this host's own metrics instance, by reason.</summary>
    private sealed class SystemClosedCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _byReason = new(StringComparer.Ordinal);

        public SystemClosedCounter(ConversationCloseMetrics metrics)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, metrics.SystemClosed))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason" && tag.Value is string reason)
                    {
                        lock (_byReason)
                            _byReason[reason] = _byReason.GetValueOrDefault(reason) + value;
                    }
                }
            });
            _listener.Start();
        }

        public long Count(string reason)
        {
            lock (_byReason)
                return _byReason.GetValueOrDefault(reason);
        }

        public void Dispose() => _listener.Dispose();
    }
}
