using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Services.Push;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Api.Tests.Sse;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Webhooks;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Redis.Tests;
using Verbara.Platform.Queues;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Pro.Push.Diagnostics;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Redis.Tests;

/// <summary>
/// supervisor-whisper-reaches-the-agent (design D2, D4, D5; tasks 2.3 and 2.4) through the real relay: two Api hosts on
/// one Redis. Host L runs the session (it files the live record, as the engine's replica does); the supervisor's POST
/// lands on either host. The whisper is published once, on the POST's host, and the backplane carries it to the other
/// one, whose receiver emits it on its streams only: never on its event bus, so never to its webhooks.
/// </summary>
[Collection("Redis")]
#pragma warning disable CA1001 // the hosts are disposed by IAsyncLifetime.DisposeAsync, xunit's teardown
public sealed class SupervisorWhisperTwoHostTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private const string WhisperText = "Offer the retention discount now.";

    private readonly RedisFixture _redis;
    private readonly List<RedisCrossPodHost> _hosts = [];
    private readonly List<AgentAssistSessionFiling> _filings = [];
    private readonly Dictionary<RedisCrossPodHost, WebhookRecorder> _webhooks = [];
    private RedisCrossPodHost? _hostL;
    private RedisCrossPodHost? _hostF;

    public SupervisorWhisperTwoHostTests(RedisFixture redis) => _redis = redis;

    private RedisCrossPodHost L => _hostL!;

    private RedisCrossPodHost F => _hostF!;

    private static string Tenant => ConversationOwnershipApiFactory.Tenant.Value;

    public Task InitializeAsync()
    {
        _hostL = NewHost(withReceiver: true);
        _hostF = NewHost(withReceiver: true);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var filing in _filings)
            filing.Dispose();
        foreach (var host in _hosts)
            await host.DisposeAsync();
    }

    /// <summary>
    /// A host whose webhook stores record its deliveries, subscribed to <c>agentassist.suggestion</c> for the tenant, and
    /// whose dispatcher is built at once (it subscribes to the event bus in its constructor).
    /// </summary>
    private RedisCrossPodHost NewHost(bool withReceiver, Action<IServiceCollection>? configure = null)
    {
        var recorder = new WebhookRecorder();
        var host = new RedisCrossPodHost(_redis.ConnectionString, services =>
        {
            services.RemoveAll<IWebhookSubscriptionStore>();
            services.AddSingleton(recorder.Subscriptions);
            services.RemoveAll<IWebhookDeliveryStore>();
            services.AddSingleton(recorder.Deliveries);
            if (!withReceiver)
            {
                foreach (var receiver in services.Where(d =>
                             d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CrossPodStreamReceiver)).ToList())
                    services.Remove(receiver);
            }

            configure?.Invoke(services);
        });
        _ = host.Services;
        _ = host.Services.GetRequiredService<WebhookDispatcher>();
        _hosts.Add(host);
        _webhooks[host] = recorder;
        return host;
    }

    /// <summary>Files a bound record of <paramref name="agent"/>'s session from <paramref name="engine"/>, as its engine does.</summary>
    private async Task<string> FileSessionAsync(RedisCrossPodHost engine, Agent agent)
    {
        var sessionId = $"as-sw-xr-{Guid.NewGuid():N}";
        var call = new CallSession($"call-{sessionId}", $"{sessionId}.call", "server-1", CallDirection.Inbound)
        {
            TenantId = Tenant, AgentId = agent.AgentId.Value, QueueName = "support",
        };
        var filing = AgentAssistSessionFiling.Start(
            sessionId, Tenant, call, engine.Services.GetRequiredService<IAgentAssistLiveSessionStore>(), TimeProvider.System,
            NullLogger.Instance);
        _filings.Add(filing);
        await filing.WhenIdleAsync();
        return sessionId;
    }

    /// <summary>Saves, on <paramref name="host"/>, the tracked voice conversation of the session's call.</summary>
    private static async Task<string> TrackConversationAsync(RedisCrossPodHost host, string sessionId)
    {
        var conversation = new Conversation
        {
            ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
            TenantId = ConversationOwnershipApiFactory.Tenant,
            ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
            Channel = ChannelType.Voice,
            State = ConversationState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            VoiceLinkedId = $"{sessionId}.call",
        };
        await host.Services.GetRequiredService<IConversationStore>().SaveAsync(conversation, CancellationToken.None);
        return conversation.ConversationId.Value;
    }

    private static async Task WhisperAsync(RedisCrossPodHost host, string sessionId)
    {
        var supervisor = host.NewUser(UserRole.Supervisor, "sw-xr-supervisor");
        using var client = host.ClientFor(supervisor);
        using var response = await client.PostAsJsonAsync($"/api/v1/supervisor/sessions/{sessionId}/whisper", new { text = WhisperText });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, because: $"the response was {body}");
        body.Should().BeEmpty();
    }

    private static int WhispersOf(LiveStreamProbe stream, string sessionId) =>
        stream.Frames.Count(f => f.Event == "agentassist.suggestion" && f.Member("sessionId") == sessionId);

    public enum Direction
    {
        /// <summary>POST to the follower F, the agent streaming from the session's host L.</summary>
        FollowerToLeader,

        /// <summary>POST to L, the agent streaming from F.</summary>
        LeaderToFollower,
    }

    /// <summary>
    /// Task 2.3: the agent's stream on the other host receives the whisper exactly once; on the POST's host a read-all
    /// subscriber receives it once and another agent does not; the receiving host counts it <c>delivered</c> once and
    /// never <c>tenant_mismatch</c>.
    /// </summary>
    [Theory]
    [InlineData(Direction.FollowerToLeader)]
    [InlineData(Direction.LeaderToFollower)]
    public async Task PostWhisper_ShouldReachTheAgentOnTheOtherHostExactlyOnce_WhenPostedTo(Direction direction)
    {
        var (posting, streaming) = direction == Direction.FollowerToLeader ? (F, L) : (L, F);
        var (agentUser, agent) = streaming.NewAgent("sw-xr-agent");
        var sessionId = await FileSessionAsync(L, agent);
        var conversationId = await TrackConversationAsync(posting, sessionId);
        await using var agentStream = await streaming.OpenStreamAsync("agent on the other host", agentUser);
        await using var readAll = await posting.OpenStreamAsync("read-all on the POST's host", posting.NewUser(UserRole.Supervisor, "sw-xr-monitor"));
        var (otherUser, _) = posting.NewAgent("sw-xr-other");
        await using var otherAgent = await posting.OpenStreamAsync("another agent on the POST's host", otherUser);
        using var received = new ReceivedCounter(streaming.ReceiverMetrics);

        await WhisperAsync(posting, sessionId);

        await received.WaitForTotalAsync(1);
        await LiveStreamProbe.SyncAsync(streaming.Bus, Tenant, agentStream);
        await LiveStreamProbe.SyncAsync(posting.Bus, Tenant, readAll, otherAgent);

        WhispersOf(agentStream, sessionId).Should().Be(1, $"{direction}: the agent on the other host receives the whisper once");
        var frame = agentStream.Frames.Single(f => f.Event == "agentassist.suggestion" && f.Member("sessionId") == sessionId);
        frame.Member("agentId").Should().Be(agent.AgentId.Value);
        frame.Member("conversationId").Should().Be(conversationId);
        frame.Member("source").Should().Be("Supervisor");
        frame.Member("priority").Should().Be("Critical");
        frame.Member("text").Should().Be(WhisperText);
        frame.Member("tenantId").Should().Be(Tenant);
        WhispersOf(readAll, sessionId).Should().Be(1, $"{direction}: a read-all subscriber on the POST's host receives it once");
        WhispersOf(otherAgent, sessionId).Should().Be(0, $"{direction}: another agent does not receive it");
        received.Of(CrossPodStreamMetrics.Delivered).Should().Be(1, "the receiving host counts it delivered once");
        received.Of(CrossPodStreamMetrics.TenantMismatch).Should().Be(0, "the envelope and payload tenants agree (design D4 gate (b))");
    }

    /// <summary>
    /// Task 2.3, the mutation control: with no receiver on the session's host, its agent's stream receives no copy even
    /// though the envelope reaches that host's push bus, so the delivery the test above measures is the receiver's.
    /// </summary>
    [Fact]
    public async Task PostWhisper_ShouldReachNoStreamOnTheOtherHost_WhenItHasNoReceiver()
    {
        var deaf = NewHost(withReceiver: false);
        var (agentUser, agent) = deaf.NewAgent("sw-xr-deaf");
        var sessionId = await FileSessionAsync(deaf, agent);
        await TrackConversationAsync(F, sessionId);
        await using var agentStream = await deaf.OpenStreamAsync("agent on the host with no receiver", agentUser);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var envelopes = deaf.PushBus.AsObservable().Subscribe(e =>
        {
            if (e is RemotePushEvent { OriginalEventType: "agentassist.suggestion" } remote
                && System.Text.Encoding.UTF8.GetString(remote.RawPayload.ToArray()).Contains(sessionId, StringComparison.Ordinal))
                arrived.TrySetResult();
        });

        await WhisperAsync(F, sessionId);

        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await LiveStreamProbe.SyncAsync(deaf.Bus, Tenant, agentStream);
        WhispersOf(agentStream, sessionId).Should().Be(0, "without the receiver nothing re-emits the envelope on the streams");
    }

    /// <summary>
    /// Task 2.4 (design D5): one whisper on F is one <c>agentassist.suggestion</c> webhook dispatch on F with source
    /// <c>Supervisor</c>; L, which receives it over the backplane, dispatches none.
    /// </summary>
    [Fact]
    public async Task PostWhisper_ShouldDispatchOneWebhookOnThePostsHostAndNoneOnTheReceivingHost()
    {
        var (agentUser, agent) = L.NewAgent("sw-xr-webhook");
        var sessionId = await FileSessionAsync(L, agent);
        await TrackConversationAsync(F, sessionId);
        await using var agentStream = await L.OpenStreamAsync("agent on L", agentUser);
        using var received = new ReceivedCounter(L.ReceiverMetrics);

        await WhisperAsync(F, sessionId);

        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await F.Services.GetRequiredService<WebhookDispatcher>().WaitForDispatchAsync(hang.Token);
        await received.WaitForTotalAsync(1);
        await LiveStreamProbe.SyncAsync(L.Bus, Tenant, agentStream);
        await L.Services.GetRequiredService<WebhookDispatcher>().WaitForDispatchAsync(hang.Token);

        WhispersOf(agentStream, sessionId).Should().Be(1, "precondition: L received the whisper over the backplane");
        var onF = _webhooks[F].DeliveriesFor(sessionId);
        onF.Should().ContainSingle("the POST's host dispatches the whisper once").Which.EventType.Should().Be("agentassist.suggestion");
        onF[0].Payload.Should().Contain("Supervisor", "the webhook carries source Supervisor");
        _webhooks[L].DeliveriesFor(sessionId).Should().BeEmpty("a copy received over the backplane never reaches the event bus");
    }

    /// <summary>
    /// Task 2.5 (design D4, spec scenario "A failed forward to Redis still answers 204 and reaches no remote stream"): the
    /// POST's host loses its backplane connection after the session lookup, so the relay's forward fails. The answer is
    /// still 204, the POST's host delivers the whisper to its own read-all stream once, the agent's stream on L receives no
    /// copy, and the POST's relay counts one event dropped with reason <c>deliver_error</c>. This pins the best-effort
    /// contract: an acknowledged delivery across replicas must change this test on purpose.
    /// </summary>
    [Fact]
    public async Task PostWhisper_ShouldAnswer204AndReachNoRemoteStream_WhenTheForwardToRedisFails()
    {
        await using var lookup = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        var broken = NewHost(withReceiver: true, services =>
        {
            services.RemoveAll<IAgentAssistLiveSessionStore>();
            services.AddSingleton<IAgentAssistLiveSessionStore>(sp => new BackplaneClosingStore(
                new RedisAgentAssistLiveSessionStore(lookup, TimeProvider.System), sp.GetRequiredService<IConnectionMultiplexer>()));
        });
        var (agentUser, agent) = L.NewAgent("sw-xr-forward");
        var sessionId = await FileSessionAsync(L, agent);
        await TrackConversationAsync(broken, sessionId);
        await using var agentStream = await L.OpenStreamAsync("agent on L", agentUser);
        await using var readAll = await broken.OpenStreamAsync("read-all on the POST's host", broken.NewUser(UserRole.Supervisor, "sw-xr-forward-monitor"));
        await LiveStreamProbe.SyncAsync(broken.Bus, Tenant, readAll);   // subscribed while the backplane is still up
        using var received = new ReceivedCounter(L.ReceiverMetrics);
        using var dropped = new DeliverErrors(broken.Services.GetRequiredService<PushProMetrics>());

        await WhisperAsync(broken, sessionId);

        await dropped.First.WaitAsync(TimeSpan.FromSeconds(60));
        var drops = dropped.Count;
        await LiveStreamProbe.SyncAsync(broken.Bus, Tenant, readAll);
        await LiveStreamProbe.SyncAsync(L.Bus, Tenant, agentStream);

        drops.Should().Be(1, "the POST's relay drops the whisper's forward once, with reason deliver_error");
        WhispersOf(readAll, sessionId).Should().Be(1, "the POST's host delivers the whisper to its own streams once");
        WhispersOf(agentStream, sessionId).Should().Be(0, "nothing reached the backplane, so no remote stream receives it");
        received.Of(CrossPodStreamMetrics.Delivered).Should().Be(0, "L's receiver got no envelope");
    }

    /// <summary>The live-session store of the POST's host: it reads Redis on its own connection, then closes the host's backplane.</summary>
    private sealed class BackplaneClosingStore(IAgentAssistLiveSessionStore inner, IConnectionMultiplexer backplane) : IAgentAssistLiveSessionStore
    {
        public bool IsShared => inner.IsShared;

        public Task FileAsync(LiveSessionRecord record, DateTimeOffset expiresAt, CancellationToken ct) => inner.FileAsync(record, expiresAt, ct);

        public Task RemoveAsync(string ownerTenant, string sessionId, CancellationToken ct) => inner.RemoveAsync(ownerTenant, sessionId, ct);

        public Task<IReadOnlyList<LiveSessionRecord>> ListAsync(string tenant, CancellationToken ct) => inner.ListAsync(tenant, ct);

        public async Task<LiveSessionRecord?> FindAsync(string callerTenant, string sessionId, CancellationToken ct)
        {
            var record = await inner.FindAsync(callerTenant, sessionId, ct);
            await backplane.CloseAsync(allowCommandsToComplete: false);
            return record;
        }
    }

    /// <summary>Counts one host's relay drops with reason <c>deliver_error</c> (<c>push_relay_dropped_total</c>), from creation on.</summary>
    private sealed class DeliverErrors : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _count;

        public DeliverErrors(PushProMetrics metrics)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, metrics.EventsDropped))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason" && tag.Value as string == "deliver_error")
                    {
                        Interlocked.Add(ref _count, value);
                        _first.TrySetResult();
                    }
                }
            });
            _listener.Start();
        }

        public Task First => _first.Task;

        public long Count => Interlocked.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>A host's webhook stores: one subscription to <c>agentassist.suggestion</c> for the tenant, deliveries recorded.</summary>
    private sealed class WebhookRecorder
    {
        private readonly List<WebhookDelivery> _saved = [];

        public WebhookRecorder()
        {
            var subscription = new WebhookSubscription("sub-sw", Tenant, "Whisper hook", "https://example.test/hook", "secret",
                ["agentassist.suggestion"], true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Subscriptions.GetActiveByEventTypeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IReadOnlyList<WebhookSubscription>>(
                    call.ArgAt<string>(0) == Tenant && call.ArgAt<string>(1) == "agentassist.suggestion" ? [subscription] : []));
            Deliveries.SaveAsync(Arg.Do<WebhookDelivery>(d =>
                {
                    lock (_saved)
                        _saved.Add(d);
                }), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);
        }

        public IWebhookSubscriptionStore Subscriptions { get; } = Substitute.For<IWebhookSubscriptionStore>();

        public IWebhookDeliveryStore Deliveries { get; } = Substitute.For<IWebhookDeliveryStore>();

        public IReadOnlyList<WebhookDelivery> DeliveriesFor(string sessionId)
        {
            lock (_saved)
                return [.. _saved.Where(d => d.Payload.Contains(sessionId, StringComparison.Ordinal))];
        }
    }
}

/// <summary>
/// supervisor-whisper-reaches-the-agent (spec audit round 1, F2; task 2.1) at the route on the real Redis store: a record
/// still filed under its former owner after a conflict answers 204 to that owner and 404 to the other tenant until the
/// engine's replica re-evaluates it, then 404 to both; a record past its expiry answers 404. The store's clock is a
/// <see cref="FakeTimeProvider"/>, shared with the filing, so a heartbeat is an advance.
/// </summary>
[Collection("Redis")]
#pragma warning disable CA1001 // the host and the connection are disposed by IAsyncLifetime.DisposeAsync
public sealed class SupervisorWhisperRedisRouteTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly RedisFixture _redis;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly List<AgentAssistSessionFiling> _filings = [];
    private readonly List<AgentAssistSuggestionEvent> _published = [];
    private ConnectionMultiplexer? _connection;
    private RedisCrossPodHost? _host;
    private IDisposable? _recorder;

    public SupervisorWhisperRedisRouteTests(RedisFixture redis) => _redis = redis;

    private RedisCrossPodHost Host => _host!;

    private static string Tenant => ConversationOwnershipApiFactory.Tenant.Value;

    /// <summary>A second operational Customer tenant, seeded on the host.</summary>
    private const string Other = "globex-sw";

    private IAgentAssistLiveSessionStore Store => Host.Services.GetRequiredService<IAgentAssistLiveSessionStore>();

    public async Task InitializeAsync()
    {
        _connection = await ConnectionMultiplexer.ConnectAsync(_redis.ConnectionString);
        var store = new RedisAgentAssistLiveSessionStore(_connection, _time);
        _host = new RedisCrossPodHost(_redis.ConnectionString, services =>
        {
            services.RemoveAll<IAgentAssistLiveSessionStore>();
            services.AddSingleton<IAgentAssistLiveSessionStore>(store);
        });
        Verbara.Platform.Api.Tests.AuthenticatedPlatformApiFactory.SeedTestCustomerTenant(Host.Services, Other);
        Verbara.Platform.Api.Tests.AuthenticatedPlatformApiFactory.SeedEnterpriseFeatureGate(Host.Services, Other);
        _recorder = Host.Bus.Events.Subscribe(evt =>
        {
            if (evt is AgentAssistSuggestionEvent suggestion)
            {
                lock (_published)
                    _published.Add(suggestion);
            }
        });
    }

    public async Task DisposeAsync()
    {
        _recorder?.Dispose();
        foreach (var filing in _filings)
            filing.Dispose();
        if (_host is not null)
            await _host.DisposeAsync();
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    private IReadOnlyList<AgentAssistSuggestionEvent> PublishedFor(string sessionId)
    {
        lock (_published)
            return [.. _published.Where(e => e.SessionId == sessionId)];
    }

    private async Task<(string SessionId, CallSession Call, AgentAssistSessionFiling Filing)> StartAsync(string? owner = null)
    {
        owner ??= Tenant;
        var sessionId = $"as-sw-route-{Guid.NewGuid():N}";
        var call = new CallSession($"call-{sessionId}", $"{sessionId}.call", "server-1", CallDirection.Inbound)
        {
            TenantId = owner, AgentId = "a-9e8d7c6b5a493827", QueueName = "support",
        };
        foreach (var tenant in new[] { Tenant, Other })
        {
            await Host.Services.GetRequiredService<IConversationStore>().SaveAsync(new Conversation
            {
                ConversationId = EntityId.From($"conv-{Guid.NewGuid():N}"),
                TenantId = new TenantId(tenant),
                ContactId = EntityId.From($"contact-{Guid.NewGuid():N}"),
                Channel = ChannelType.Voice,
                State = ConversationState.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                VoiceLinkedId = call.LinkedId,
            }, CancellationToken.None);
        }

        var filing = AgentAssistSessionFiling.Start(sessionId, owner, call, Store, _time, NullLogger.Instance);
        _filings.Add(filing);
        await filing.WhenIdleAsync();
        return (sessionId, call, filing);
    }

    private async Task<HttpStatusCode> WhisperAsync(string tenant, string sessionId)
    {
        var supervisor = tenant == Tenant
            ? Host.NewUser(UserRole.Supervisor, "sw-route")
            : Host.SaveUser($"sw-route-other-{Guid.NewGuid():N}", tenant, UserRole.Supervisor, UserStatus.Active);
        using var client = Host.ClientFor(supervisor);
        using var response = await client.PostAsJsonAsync($"/api/v1/supervisor/sessions/{sessionId}/whisper", new { text = "hint" });
        return response.StatusCode;
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswerTheFormerOwnerOnlyUntilTheReevaluation_WhenTheSessionsTenantsComeIntoConflict()
    {
        var (sessionId, call, filing) = await StartAsync();

        call.TenantId = Other;   // conflicts with the start-time tenant; no event yet

        (await WhisperAsync(Tenant, sessionId)).Should().Be(HttpStatusCode.NoContent, "still filed under the former owner");
        (await WhisperAsync(Other, sessionId)).Should().Be(HttpStatusCode.NotFound, "never found by the other tenant");
        PublishedFor(sessionId).Should().ContainSingle().Which.TenantId.Should().Be(Tenant);

        _time.Advance(AgentAssistSessionFiling.HeartbeatInterval);
        await filing.WhenIdleAsync();

        (await WhisperAsync(Tenant, sessionId)).Should().Be(HttpStatusCode.NotFound, "the heartbeat's re-evaluation removed it");
        (await WhisperAsync(Other, sessionId)).Should().Be(HttpStatusCode.NotFound);
        PublishedFor(sessionId).Should().ContainSingle("no refusal publishes anything");
    }

    /// <summary>
    /// The positive control of the other tenant's 404s above: its supervisor passes every gate before the handler on this
    /// host and gets 204 for a session filed under it, so those 404s are the lookup's.
    /// </summary>
    [Fact]
    public async Task PostWhisper_ShouldAnswer204ToTheOtherTenant_WhenTheSessionIsFiledUnderIt()
    {
        var (sessionId, _, _) = await StartAsync(owner: Other);

        (await WhisperAsync(Other, sessionId)).Should().Be(HttpStatusCode.NoContent, "the session is the other tenant's own");
        (await WhisperAsync(Tenant, sessionId)).Should().Be(HttpStatusCode.NotFound, "and never the first tenant's");
        PublishedFor(sessionId).Should().ContainSingle().Which.TenantId.Should().Be(Other);
    }

    [Fact]
    public async Task PostWhisper_ShouldAnswer404FromTheRecordsExpiry_WhenTheEngineReplicaStopped()
    {
        var (sessionId, _, filing) = await StartAsync();
        filing.Dispose();   // the engine's replica stopped: the record is no longer re-filed

        _time.Advance(AgentAssistSessionFiling.RecordTtl - TimeSpan.FromSeconds(1));
        (await WhisperAsync(Tenant, sessionId)).Should().Be(HttpStatusCode.NoContent, "before its expiry");

        _time.Advance(TimeSpan.FromSeconds(1));
        (await WhisperAsync(Tenant, sessionId)).Should().Be(HttpStatusCode.NotFound, "from its last filing + 15 s on");
        PublishedFor(sessionId).Should().ContainSingle();
    }
}
