using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Services.Push;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;

namespace Verbara.Platform.Api.Tests.Sse;

/// <summary>
/// The content-delivery host with the receiving half of the backplane: <see cref="CrossPodStreamReceiver"/> reads the
/// envelopes put on this host's <see cref="IPushEventBus"/>, as the relay puts the ones another replica published.
/// </summary>
public class CrossPodContentDeliveryApiFactory : ContentDeliveryApiFactory
{
    /// <summary>The host's push bus, where the relay puts each envelope it receives.</summary>
    internal IPushEventBus PushBus => Services.GetRequiredService<IPushEventBus>();

    /// <summary>The receiver's counter.</summary>
    internal CrossPodStreamMetrics ReceiverMetrics => Services.GetRequiredService<CrossPodStreamMetrics>();

    /// <inheritdoc />
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        base.ConfigureTestServices(services);
        services.TryAddSingleton<CrossPodStreamMetrics>();
        services.AddHostedService<CrossPodStreamReceiver>();
    }
}

/// <summary>
/// agent-assist-works-end-to-end (design D12, receiving half): the four <c>agentassist.*</c> another
/// replica published reach this replica's live streams through <see cref="CrossPodStreamReceiver"/>, under the same
/// delivery rule as a local publish, exactly once, and nowhere else. Each envelope is built as <c>RedisEventRelay</c>
/// builds it, from the payload the publishing replica serialized.
/// </summary>
public sealed class CrossPodStreamReceiverTests : IClassFixture<CrossPodContentDeliveryApiFactory>
{
    private static readonly string[] Types =
        ["agentassist.suggestion", "agentassist.sentiment", "agentassist.compliance_alert", "agentassist.transcript"];

    private readonly CrossPodContentDeliveryApiFactory _factory;

    public CrossPodStreamReceiverTests(CrossPodContentDeliveryApiFactory factory) => _factory = factory;

    private static string Tenant => ConversationOwnershipApiFactory.Tenant.Value;

    /// <summary>How an event reaches the stream: published on this replica, or received from another.</summary>
    public enum Path
    {
        Local,
        Received,
    }

    /// <summary>The envelope another replica's relay sends for <paramref name="evt"/>, as this replica's relay receives it.</summary>
    internal static RemotePushEvent EnvelopeOf(PlatformEvent evt, string? payload = null, string? envelopeTenant = null) =>
        new(evt.EventType, "node-a", Encoding.UTF8.GetBytes(payload ?? AgentAssistCrossPodWireTests.Payload(evt)))
        {
            Metadata = new PushEventMetadata(
                TenantId: envelopeTenant ?? evt.Metadata.TenantId,
                UserId: evt.Metadata.UserId,
                OccurredAt: evt.Metadata.OccurredAt,
                CorrelationId: evt.Metadata.CorrelationId,
                TopicPath: evt.Metadata.TopicPath,
                TraceContext: evt.Metadata.TraceContext),
        };

    private static PlatformEvent[] AgentAssistEvents(string agentId, string conversationId) =>
        [.. Types.Select(t => AgentAssistCrossPodWireTests.Event(t, Tenant, agentId) switch
        {
            AgentAssistSuggestionEvent e => e with { ConversationId = conversationId },
            AgentAssistSentimentEvent e => e with { ConversationId = conversationId },
            AgentAssistComplianceAlertEvent e => e with { ConversationId = conversationId },
            AgentAssistTranscriptEvent e => e with { ConversationId = conversationId },
            var other => other,
        })];

    /// <summary>Puts <paramref name="envelopes"/> on the push bus and waits until the receiver has counted each.</summary>
    private async Task ReceiveAsync(ReceivedCounter counter, params RemotePushEvent[] envelopes)
    {
        var before = counter.Total;
        foreach (var envelope in envelopes)
            await _factory.PushBus.PublishAsync(envelope);
        await counter.WaitForTotalAsync(before + envelopes.Length);
    }

    private async Task PublishAsync(Path path, ReceivedCounter counter, IEnumerable<PlatformEvent> events)
    {
        if (path == Path.Local)
        {
            foreach (var evt in events)
                _factory.Bus.Publish(evt);
            return;
        }

        await ReceiveAsync(counter, [.. events.Select(e => EnvelopeOf(e))]);
    }

    private static int FramesOf(LiveStreamProbe stream, string type, string conversationId) =>
        stream.Frames.Count(f => f.Event == type && f.Member("conversationId") == conversationId);

    private User Reader(string template, string label)
    {
        var user = _factory.NewUser(UserRole.Agent, label);
        _factory.GrantTemplate(user, template);
        return user;
    }

    /// <summary>
    /// The recipients of <c>agentassist-events.v1.json</c> (<c>recipients.rows</c>), on the local path and on the received
    /// path alike. Two rows are covered by the rule test instead (<c>ConversationContentDeliveryRuleTests</c>), whose
    /// predicate is the one this stream applies to both paths: SystemAdmin passes on the same role shortcut as Admin
    /// (<c>PermissionAuthorizationHandler</c>) and this host mints no SystemAdmin token, and the stream route refuses an
    /// anonymous or service-key caller (401), so no unscoped stream opens here.
    /// </summary>
    [Theory]
    [InlineData(Path.Local)]
    [InlineData(Path.Received)]
    public async Task StreamEvents_ShouldDeliverToTheFixturesRecipientsOnly_WhenTheEventIs(Path path)
    {
        var (userA, agentA) = _factory.NewAgent($"{path}-named");
        var (userO, agentO) = _factory.NewAgent($"{path}-owner");
        var (userF, agentF) = _factory.NewAgent($"{path}-offered");
        var (userU, _) = _factory.NewAgent($"{path}-other");
        var supervisor = _factory.NewUser(UserRole.Supervisor, $"{path}-monitor");
        var analyst = Reader("quality_analyst", $"{path}-read");
        var admin = _factory.NewUser(UserRole.Admin, $"{path}-admin");
        var noProfile = _factory.NewUser(UserRole.Agent, $"{path}-no-profile");
        var otherTenant = _factory.SaveUser(
            $"{path}-other-tenant-{Guid.NewGuid():N}", ConversationOwnershipApiFactory.PlatformTenantId, UserRole.Supervisor, UserStatus.Active);
        var owned = _factory.SeedConversation(ConversationOwner.ForAgent(agentO.AgentId), ConversationState.Active, channel: ChannelType.Voice);
        var offered = _factory.SeedOfferTo(agentF, _factory.SeedQueue());

        // The other tenant's stream is synced on its own tenant's marker first: a probe's subscription signal is the
        // first marker it evaluates, and it refuses the customer tenant's.
        await using var streamT = await LiveStreamProbe.OpenAsync("other tenant", _factory.ClientFor(otherTenant));
        await LiveStreamProbe.SyncAsync(_factory.Bus, ConversationOwnershipApiFactory.PlatformTenantId, streamT);
        await using var streamA = await _factory.OpenStreamAsync("named agent", userA);
        await using var streamO = await _factory.OpenStreamAsync("owner", userO);
        await using var streamF = await _factory.OpenStreamAsync("offered agent", userF);
        await using var streamU = await _factory.OpenStreamAsync("other agent", userU);
        await using var streamS = await _factory.OpenStreamAsync("monitor", supervisor);
        await using var streamQ = await _factory.OpenStreamAsync("read", analyst);
        await using var streamD = await _factory.OpenStreamAsync("Admin", admin);
        await using var streamH = await _factory.OpenStreamAsync("handle without profile", noProfile);
        var everyone = new[] { streamA, streamO, streamF, streamU, streamS, streamQ, streamD, streamH };
        using var counter = new ReceivedCounter(_factory.ReceiverMetrics);

        var ownedId = owned.ConversationId.Value;
        var offeredId = offered.ConversationId.Value;
        var unnamedId = $"{ownedId}-unnamed";
        await PublishAsync(path, counter, AgentAssistEvents(agentA.AgentId.Value, ownedId)
            .Concat(AgentAssistEvents(agentA.AgentId.Value, offeredId))
            .Concat(AgentAssistEvents(string.Empty, unnamedId)));
        await LiveStreamProbe.SyncAsync(_factory.Bus, Tenant, everyone);
        await LiveStreamProbe.SyncAsync(_factory.Bus, ConversationOwnershipApiFactory.PlatformTenantId, streamT);

        foreach (var type in Types)
        {
            foreach (var id in new[] { ownedId, offeredId })
            {
                FramesOf(streamA, type, id).Should().Be(1, $"{path}: the agent {type} names receives it once");
                FramesOf(streamS, type, id).Should().Be(1, $"{path}: a monitor holder receives {type}");
                FramesOf(streamQ, type, id).Should().Be(1, $"{path}: a read holder receives {type}");
                FramesOf(streamD, type, id).Should().Be(1, $"{path}: an Admin receives {type}");
                FramesOf(streamU, type, id).Should().Be(0, $"{path}: another agent does not receive {type}");
                FramesOf(streamH, type, id).Should().Be(0, $"{path}: a handle holder without a profile does not receive {type}");
                FramesOf(streamT, type, id).Should().Be(0, $"{path}: another tenant's subscriber does not receive {type}");
            }

            FramesOf(streamO, type, ownedId).Should().Be(0, $"{path}: the owner is not the agent {type} names");
            FramesOf(streamF, type, offeredId).Should().Be(0, $"{path}: the offered agent is not the agent {type} names");

            FramesOf(streamS, type, unnamedId).Should().Be(1, $"{path}: an empty agentId still reaches the readers ({type})");
            FramesOf(streamQ, type, unnamedId).Should().Be(1, $"{path}: an empty agentId still reaches the readers ({type})");
            FramesOf(streamD, type, unnamedId).Should().Be(1, $"{path}: an empty agentId still reaches the readers ({type})");
            foreach (var agentStream in new[] { streamA, streamO, streamF, streamU, streamH, streamT })
                FramesOf(agentStream, type, unnamedId).Should().Be(0, $"{path}: an empty agentId names no agent ({type}, {agentStream.Name})");
        }
    }

    [Fact]
    public async Task CrossPodStreamReceiver_ShouldDeliverEachReceivedEventExactlyOnce_WithTheFrameOfTheOriginal()
    {
        var (userA, agentA) = _factory.NewAgent("once-named");
        await using var streamA = await _factory.OpenStreamAsync("named agent", userA);
        using var counter = new ReceivedCounter(_factory.ReceiverMetrics);
        var id = $"once-{Guid.NewGuid():N}";
        var events = AgentAssistEvents(agentA.AgentId.Value, id);

        await ReceiveAsync(counter, [.. events.Select(e => EnvelopeOf(e))]);
        await LiveStreamProbe.SyncAsync(_factory.Bus, Tenant, streamA);

        foreach (var evt in events)
        {
            var frames = streamA.Frames.Where(f => f.Event == evt.Type && f.Member("conversationId") == id).ToList();
            frames.Should().HaveCount(1, $"{evt.Type} delivered more than once, or not at all");
            frames[0].Data.ToJsonString().Should().Be(System.Text.Json.Nodes.JsonNode.Parse(
                System.Text.Json.JsonSerializer.Serialize(evt, evt.GetType(), Verbara.Platform.Api.Serialization.ApiJsonContext.Default))!.ToJsonString(),
                "the received frame is the original's");
        }

        counter.Of(CrossPodStreamMetrics.Delivered).Should().Be(4);
    }

    [Fact]
    public async Task CrossPodStreamReceiver_ShouldDeliverNothingAndCount_WhenThePayloadIsEmptyOrItsTenantIsNotTheEnvelopes()
    {
        var supervisor = _factory.NewUser(UserRole.Supervisor, "dropped-monitor");
        await using var streamS = await _factory.OpenStreamAsync("monitor", supervisor);
        using var counter = new ReceivedCounter(_factory.ReceiverMetrics);
        var empty = AgentAssistEvents("a-1", $"empty-{Guid.NewGuid():N}")[3];
        var mismatched = AgentAssistEvents("a-1", $"mismatch-{Guid.NewGuid():N}")[3];

        await ReceiveAsync(counter,
            EnvelopeOf(empty, payload: string.Empty),
            EnvelopeOf(mismatched, envelopeTenant: ConversationOwnershipApiFactory.PlatformTenantId),
            EnvelopeOf(mismatched, payload: "{\"TenantId\":"));
        await LiveStreamProbe.SyncAsync(_factory.Bus, Tenant, streamS);

        streamS.Frames.Should().NotContain(f => f.Event == "agentassist.transcript" &&
            (f.Member("conversationId") == ((AgentAssistTranscriptEvent)empty).ConversationId ||
             f.Member("conversationId") == ((AgentAssistTranscriptEvent)mismatched).ConversationId));
        counter.Of(CrossPodStreamMetrics.EmptyPayload).Should().Be(1, "expected outcome empty_payload");
        counter.Of(CrossPodStreamMetrics.TenantMismatch).Should().Be(1, "expected outcome tenant_mismatch");
        counter.Of(CrossPodStreamMetrics.DecodeError).Should().Be(1, "expected outcome decode_error");
        counter.Of(CrossPodStreamMetrics.Delivered).Should().Be(0);
    }

    /// <summary>
    /// The valid envelope and the empty-payload copy a Realtime replica's relay echoes back, in both orders: the stream
    /// receives the event once, and the counter tells the echo (<c>empty_payload</c>) from a broken payload
    /// (<c>decode_error</c>).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CrossPodStreamReceiver_ShouldDeliverOnceAndCountTheEcho_WhenTheEchoArrives(bool echoFirst)
    {
        var (userA, agentA) = _factory.NewAgent($"echo-{echoFirst}");
        await using var streamA = await _factory.OpenStreamAsync("named agent", userA);
        using var counter = new ReceivedCounter(_factory.ReceiverMetrics);
        var id = $"echo-{Guid.NewGuid():N}";
        var transcript = AgentAssistEvents(agentA.AgentId.Value, id)[3];
        var valid = EnvelopeOf(transcript);
        var echo = EnvelopeOf(transcript, payload: string.Empty);

        await ReceiveAsync(counter, echoFirst ? [echo, valid] : [valid, echo]);
        await LiveStreamProbe.SyncAsync(_factory.Bus, Tenant, streamA);

        FramesOf(streamA, "agentassist.transcript", id).Should().Be(1, "delivered more than once, or not at all");
        counter.Of(CrossPodStreamMetrics.Delivered).Should().Be(1, "expected outcome delivered");
        counter.Of(CrossPodStreamMetrics.EmptyPayload).Should().Be(1, "expected outcome empty_payload");
        counter.Of(CrossPodStreamMetrics.DecodeError).Should().Be(0, "an echo is not a decode error");
    }

    /// <summary>
    /// A received event reaches only the live streams: nothing on this host's <see cref="PlatformEventBus"/> (which the
    /// <c>WebhookDispatcher</c> and the other in-process subscribers read) and no typed copy on its push bus (which the
    /// relay would send back onto the backplane).
    /// </summary>
    /// <remarks>
    /// Both buses belong to the class fixture's host, and the push bus dispatches on its own loop: the copies of the
    /// stream markers <c>OpenStreamAsync</c> published, or any event the host publishes, can reach the recorders inside
    /// the window (issue #700). So the recorders keep only the events of the conversation this test sends, which is what a
    /// copy the receiver put back would carry, and an event of another conversation is published inside the window to show
    /// that the recorders see such traffic and leave it out.
    /// </remarks>
    [Fact]
    public async Task CrossPodStreamReceiver_ShouldPublishNothing_WhenItReceivesAnEvent()
    {
        var supervisor = _factory.NewUser(UserRole.Supervisor, "silent-monitor");
        await using var streamS = await _factory.OpenStreamAsync("monitor", supervisor);
        using var counter = new ReceivedCounter(_factory.ReceiverMetrics);
        var id = $"silent-{Guid.NewGuid():N}";
        var otherId = $"silent-other-{Guid.NewGuid():N}";
        var onBus = new List<PlatformEvent>();
        var onPushBus = new List<PushEvent>();
        var otherSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string MarkerType = "test.cross_pod_marker";
        using var busRecorder = _factory.Bus.Events.Subscribe(e =>
        {
            if (ConversationIdOf(e) == id)
                lock (onBus)
                    onBus.Add(e);
        });
        using var pushRecorder = _factory.PushBus.AsObservable().Subscribe(e =>
        {
            if (e is RemotePushEvent { OriginalEventType: MarkerType })
                marker.TrySetResult();
            else if (e is RemotePushEvent)
                return;
            else if (ConversationIdOf(e) == id)
                lock (onPushBus)
                    onPushBus.Add(e);
            else if (ConversationIdOf(e) == otherId)
                otherSeen.TrySetResult();
        });

        // Another conversation's event, published here inside the window: it reaches both buses and is not this test's.
        _factory.Bus.Publish(AgentAssistEvents("a-1", otherId)[3]);
        await ReceiveAsync(counter, [.. AgentAssistEvents("a-1", id).Select(e => EnvelopeOf(e))]);
        // The push bus delivers in order: a copy the receiver had put on it would come before this marker.
        await _factory.PushBus.PublishAsync(new RemotePushEvent(MarkerType, "node-a", []) { Metadata = new PushEventMetadata(Tenant, null, DateTimeOffset.UtcNow, null, null, null) });
        await marker.Task.WaitAsync(TimeSpan.FromSeconds(60));

        counter.Of(CrossPodStreamMetrics.Delivered).Should().Be(4, "precondition: the stream received them");
        otherSeen.Task.IsCompleted.Should().BeTrue("precondition: the push bus recorder sees the events published in its window");
        lock (onBus)
            onBus.OfType<IConversationContentEvent>().Should().BeEmpty("delivered more than once: a received event must not reach the event bus or the webhooks");
        lock (onPushBus)
            onPushBus.Should().BeEmpty("a received event must not be put back on the push bus");
    }

    /// <summary>The conversation an <c>agentassist.*</c> event is for, or <see langword="null"/> for any other event.</summary>
    private static string? ConversationIdOf(PushEvent e) => e switch
    {
        AgentAssistSuggestionEvent x => x.ConversationId,
        AgentAssistSentimentEvent x => x.ConversationId,
        AgentAssistComplianceAlertEvent x => x.ConversationId,
        AgentAssistTranscriptEvent x => x.ConversationId,
        _ => null,
    };
}

/// <summary>Counts <see cref="CrossPodStreamMetrics"/>'s measurements, by outcome, from creation on.</summary>
internal sealed class ReceivedCounter : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ReceivedCounter(CrossPodStreamMetrics metrics)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter) && instrument.Name == CrossPodStreamMetrics.ReceivedInstrument)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var outcome = string.Empty;
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                    outcome = tag.Value as string ?? string.Empty;
            }

            TaskCompletionSource changed;
            lock (_counts)
            {
                _counts[outcome] = _counts.GetValueOrDefault(outcome) + value;
                changed = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            changed.TrySetResult();
        });
        _listener.Start();
    }

    public long Total
    {
        get
        {
            lock (_counts)
                return _counts.Values.Sum();
        }
    }

    public long Of(string outcome)
    {
        lock (_counts)
            return _counts.GetValueOrDefault(outcome);
    }

    /// <summary>Waits, on the counter's own signal, until <paramref name="total"/> envelopes are counted.</summary>
    public async Task WaitForTotalAsync(long total)
    {
        using var hang = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            Task changed;
            lock (_counts)
            {
                if (_counts.Values.Sum() >= total)
                    return;
                changed = _changed.Task;
            }

            await changed.WaitAsync(hang.Token);
        }
    }

    public void Dispose() => _listener.Dispose();
}
