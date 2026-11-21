using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Api.Tests.Sse;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Identity.Redis.Tests;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Pro.Push.Backplane;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Redis.Tests;

/// <summary>
/// An Api host on a Redis backplane: its own relay, its receiver and the shared Monitor list. <paramref name="configure"/>
/// runs last on the host's services, so a test can replace or remove one of them.
/// </summary>
public sealed class RedisCrossPodHost(string redis, Action<IServiceCollection>? configure = null) : CrossPodContentDeliveryApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("ConnectionStrings:Redis", redis);
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);
        // The test host strips every Verbara hosted service; the relay is the one that joins the two hosts.
        services.AddHostedService<RedisEventRelay>();
        configure?.Invoke(services);
    }
}

/// <summary>
/// agent-assist-works-end-to-end (design D12) through the real relay: two Api hosts and a Realtime-style echoer on one
/// Redis. Host A publishes as the engine's bridge does, on its event bus; an agent's stream and a supervisor's list are
/// served by host B. The echoer stands for a Realtime replica, whose relay sends every envelope it receives back with an
/// empty payload.
/// </summary>
[Collection("Redis")]
#pragma warning disable CA1001 // the hosts and the echoer are disposed by IAsyncLifetime.DisposeAsync, xunit's teardown
public sealed class TwoHostBackplaneTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly RedisFixture _redis;
    private RedisCrossPodHost? _hostA;
    private RedisCrossPodHost? _hostB;
    private RealtimeEchoer? _echoer;

    public TwoHostBackplaneTests(RedisFixture redis) => _redis = redis;

    private RedisCrossPodHost A => _hostA!;

    private RedisCrossPodHost B => _hostB!;

    private static string Tenant => ConversationOwnershipApiFactory.Tenant.Value;

    public async Task InitializeAsync()
    {
        _echoer = await RealtimeEchoer.StartAsync(_redis.ConnectionString);
        _hostA = new RedisCrossPodHost(_redis.ConnectionString);
        _hostB = new RedisCrossPodHost(_redis.ConnectionString);
        _ = _hostA.Services;
        _ = _hostB.Services;
    }

    public async Task DisposeAsync()
    {
        if (_hostA is not null)
            await _hostA.DisposeAsync();
        if (_hostB is not null)
            await _hostB.DisposeAsync();
        if (_echoer is not null)
            await _echoer.DisposeAsync();
    }

    private static AgentAssistTranscriptEvent Transcript(string agentId, string sessionId, string text) =>
        new(Tenant, sessionId, agentId, $"conv-{sessionId}", "Caller", text, IsFinal: true);

    [Fact]
    public async Task Publish_ShouldReachTheNamedAgentOnTheOtherHostExactlyOnce_WithTheSameFrame()
    {
        var (userA, agentA) = B.NewAgent("two-host-named");
        var otherTenant = B.SaveUser($"two-host-other-{Guid.NewGuid():N}", ConversationOwnershipApiFactory.PlatformTenantId, UserRole.Supervisor, UserStatus.Active);
        await using var otherStream = await LiveStreamProbe.OpenAsync("other tenant on B", B.ClientFor(otherTenant));
        await LiveStreamProbe.SyncAsync(B.Bus, ConversationOwnershipApiFactory.PlatformTenantId, otherStream);
        await using var agentStream = await B.OpenStreamAsync("named agent on B", userA);
        using var counterB = new ReceivedCounter(B.ReceiverMetrics);
        var sessionId = $"as-two-host-{Guid.NewGuid():N}";
        var events = new PlatformEvent[]
        {
            new AgentAssistSuggestionEvent(Tenant, sessionId, agentA.AgentId.Value, $"conv-{sessionId}", "sg-1", "Offer a refund", "Important", "Keyword", null),
            new AgentAssistSentimentEvent(Tenant, sessionId, agentA.AgentId.Value, $"conv-{sessionId}", "Caller", -0.6f, "Frustrated", ["again"]),
            new AgentAssistComplianceAlertEvent(Tenant, sessionId, agentA.AgentId.Value, $"conv-{sessionId}", "cr-1", "card", "Warning"),
            Transcript(agentA.AgentId.Value, sessionId, "third time I call"),
        };

        foreach (var evt in events)
            A.Bus.Publish(evt);

        // Each envelope arrives on B twice: from A's relay with its payload, and from the echoer without one.
        await counterB.WaitForTotalAsync(2 * events.Length);
        await LiveStreamProbe.SyncAsync(B.Bus, Tenant, agentStream);
        await LiveStreamProbe.SyncAsync(B.Bus, ConversationOwnershipApiFactory.PlatformTenantId, otherStream);

        foreach (var evt in events)
        {
            var frames = agentStream.Frames.Where(f => f.Event == evt.Type && f.Member("sessionId") == sessionId).ToList();
            frames.Should().HaveCount(1, $"{evt.Type} reaches the agent on B once (delivered more than once, or lost)");
            frames[0].Data.ToJsonString().Should().Be(JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
                evt, evt.GetType(), Verbara.Platform.Api.Serialization.ApiJsonContext.Default))!.ToJsonString(), "the frame A would write");
            _echoer!.EnvelopesWithPayload(evt.Type, sessionId).Should().Be(1,
                $"one envelope with a payload crosses Redis for {evt.Type}: B sends none back");
        }

        otherStream.Frames.Should().NotContain(f => f.Member("sessionId") == sessionId, "another tenant's stream receives nothing");
        counterB.Of(Verbara.Platform.Api.Services.Push.CrossPodStreamMetrics.Delivered).Should().Be(events.Length);
        counterB.Of(Verbara.Platform.Api.Services.Push.CrossPodStreamMetrics.EmptyPayload).Should().Be(events.Length, "each echo is counted");
    }

    /// <summary>
    /// Order (design D12): 100 transcripts of one session, published back to back on A, reach the
    /// agent's stream on B in publish order, each once. Completeness first, so a loss is never read as a reorder.
    /// </summary>
    [Fact]
    public async Task Publish_ShouldDeliverOneSessionsTranscriptsInPublishOrder_WhenTheyAreReceivedFromTheOtherHost()
    {
        const int Count = 100;
        var (userA, agentA) = B.NewAgent("two-host-order");
        await using var agentStream = await B.OpenStreamAsync("named agent on B", userA);
        using var drops = new BufferDrops();
        var sessionId = $"as-order-{Guid.NewGuid():N}";
        var texts = Enumerable.Range(0, Count).Select(i => $"line-{i:D3}").ToList();

        foreach (var text in texts)
            A.Bus.Publish(Transcript(agentA.AgentId.Value, sessionId, text));

        List<string> Received(IReadOnlyList<SseFrame> frames) =>
            [.. frames.Where(f => f.Event == "agentassist.transcript" && f.Member("sessionId") == sessionId).Select(f => f.Member("text")!)];
        await agentStream.WaitForFramesAsync(frames => Received(frames).Count >= Count, Bound);
        await LiveStreamProbe.SyncAsync(B.Bus, Tenant, agentStream);
        var received = Received(agentStream.Frames);

        drops.BufferFull.Should().Be(0, "no push bus on the path evicted an event (asterisk.push.events.dropped{reason=buffer_full})");
        received.Should().HaveCount(Count,
            $"lost, not reordered: missing {string.Join(", ", texts.Except(received))}");
        received.Should().OnlyHaveUniqueItems("each transcript once");
        var inversion = received.Select((text, i) => (text, i)).FirstOrDefault(p => p.text != texts[p.i]);
        inversion.text.Should().BeNull($"first inversion at position {inversion.i}: received {inversion.text}, published {(inversion.text is null ? "" : texts[inversion.i])}");
    }

    [Fact]
    public async Task ActiveSessions_ShouldListOnTheSecondHostTheSessionTheFirstHostFiled()
    {
        var supervisor = B.NewUser(UserRole.Supervisor, "two-host-list");
        var call = new CallSession($"call-{Guid.NewGuid():N}", "1712345678.70", "server-1", CallDirection.Inbound)
        {
            TenantId = Tenant, AgentId = "agent-7", QueueName = "support",
        };
        var sessionId = $"as-list-{Guid.NewGuid():N}";
        using var filing = AgentAssistSessionFiling.Start(
            sessionId, Tenant, call, A.Services.GetRequiredService<IAgentAssistLiveSessionStore>(), TimeProvider.System,
            NullLogger.Instance);
        await filing.WhenIdleAsync();

        using var client = B.ClientFor(supervisor);
        var response = await client.GetAsync("/api/v1/supervisor/sessions/active");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonArray>())!;
        rows.Select(r => r!["sessionId"]!.GetValue<string>()).Should().Contain(sessionId,
            "the list on a second host shows the session the first host runs");
        await filing.EndAsync();
    }

    /// <summary>Counts the push buses' buffer evictions (<c>asterisk.push.events.dropped{reason=buffer_full}</c>), every host.</summary>
    private sealed class BufferDrops : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _bufferFull;

        public BufferDrops()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "asterisk.push.events.dropped")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason" && tag.Value as string == "buffer_full")
                        Interlocked.Add(ref _bufferFull, value);
                }
            });
            _listener.Start();
        }

        public long BufferFull => Interlocked.Read(ref _bufferFull);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// Stands for a Realtime replica on the backplane: it reads every envelope in order and sends each one that carries a
    /// payload back on the same channel with an empty payload, from its own node id, as Realtime's relay does. It also
    /// counts the envelopes with a payload, by event type and session.
    /// </summary>
    private sealed class RealtimeEchoer : IAsyncDisposable
    {
        private readonly ConnectionMultiplexer _redis;
        private readonly Dictionary<(string Type, string Session), int> _withPayload = [];

        private RealtimeEchoer(ConnectionMultiplexer redis) => _redis = redis;

        public static async Task<RealtimeEchoer> StartAsync(string connectionString)
        {
            var echoer = new RealtimeEchoer(await ConnectionMultiplexer.ConnectAsync(connectionString));
            var subscriber = echoer._redis.GetSubscriber();
            var queue = await subscriber.SubscribeAsync(new RedisChannel("asterisk:push:*", RedisChannel.PatternMode.Pattern));
            queue.OnMessage(async message =>
            {
                if (JsonNode.Parse(message.Message.ToString()) is not JsonObject envelope)
                    return;
                var payload = envelope["payloadJson"]?.GetValue<string>();
                var type = envelope["eventType"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrEmpty(payload) || !type.StartsWith("agentassist.", StringComparison.Ordinal))
                    return;

                var session = JsonNode.Parse(payload)?["SessionId"]?.GetValue<string>() ?? string.Empty;
                lock (echoer._withPayload)
                    echoer._withPayload[(type, session)] = echoer._withPayload.GetValueOrDefault((type, session)) + 1;

                envelope["payloadJson"] = string.Empty;
                envelope["originNodeId"] = "realtime-echo";
                await subscriber.PublishAsync(
                    new RedisChannel(message.Channel.ToString(), RedisChannel.PatternMode.Literal), envelope.ToJsonString());
            });
            return echoer;
        }

        public int EnvelopesWithPayload(string type, string session)
        {
            lock (_withPayload)
                return _withPayload.GetValueOrDefault((type, session));
        }

        public async ValueTask DisposeAsync() => await _redis.DisposeAsync();
    }
}
