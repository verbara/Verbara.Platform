using System.Reactive.Subjects;
using Verbara.Platform.Realtime.Contracts.Dtos;
using Verbara.Platform.Realtime.Services;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Push.SignalR.Events;
using Verbara.Sdk.Pro.Push.SignalR.Hubs;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
// csat-completion — the typed hub method takes the Pro-package payload; alias it to disambiguate from
// the identically-named Verbara.Platform.Realtime.Contracts.Dtos.CsatResponseRecordedPayload.
using CsatResponseRecordedPayload = Verbara.Sdk.Pro.Push.SignalR.Events.CsatResponseRecordedPayload;

namespace Verbara.Platform.Realtime.Tests.Services;

public sealed class PushToHubRelayTests
{
    // -----------------------------------------------------------------------
    // Fake leader with mutable IsLeader for leader-gating tests.
    // -----------------------------------------------------------------------

    private sealed class FakeClusterLeader : IClusterLeader
    {
        public string Resource { get; } = RealtimeLeaderResources.Fanout;
        public bool IsLeader { get; set; }
        public string? CurrentLeaderInstanceId => IsLeader ? "test-instance" : null;
    }

    // Minimal capturing logger — avoids CA1873 from NSubstitute's Arg.Any<>
    // expansion against ILogger.Log's generic state parameter.
    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }

    // -----------------------------------------------------------------------
    // Fake bus using Rx Subject so we can emit events synchronously
    // -----------------------------------------------------------------------

    private sealed class FakePushEventBus : IPushEventBus, IDisposable
    {
        private readonly Subject<PushEvent> _subject = new();

        public void Dispose() => _subject.Dispose();

        public void Emit(PushEvent evt) => _subject.OnNext(evt);

        public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
            where TEvent : PushEvent
        {
            _subject.OnNext(pushEvent);
            return ValueTask.CompletedTask;
        }

        public IObservable<PushEvent> AsObservable() => _subject;

        public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent =>
            (IObservable<TEvent>)System.Reactive.Linq.Observable.OfType<TEvent>(_subject);
    }

    private sealed record HubMocks(
        IHubContext<PlatformHub, IPlatformHubClient> HubContext,
        IHubClients<IPlatformHubClient> HubClients,
        IPlatformHubClient GroupClient);

    private static HubMocks CreateMockHubContext(string group)
    {
        var groupClient = Substitute.For<IPlatformHubClient>();
        groupClient.OnConversationStateChanged(Arg.Any<ConversationStatePayload>()).Returns(Task.CompletedTask);
        groupClient.OnAgentStateChanged(Arg.Any<AgentStatePayload>()).Returns(Task.CompletedTask);
        groupClient.OnClusterNodeStateChanged(Arg.Any<ClusterNodeStatePayload>()).Returns(Task.CompletedTask);
        // csat-completion — the CSAT recorded branch now uses the typed hub client method.
        groupClient.OnCsatResponseRecorded(Arg.Any<CsatResponseRecordedPayload>()).Returns(Task.CompletedTask);

        var hubClients = Substitute.For<IHubClients<IPlatformHubClient>>();
        hubClients.Group(group).Returns(groupClient);

        var hubContext = Substitute.For<IHubContext<PlatformHub, IPlatformHubClient>>();
        hubContext.Clients.Returns(hubClients);

        return new HubMocks(hubContext, hubClients, groupClient);
    }

    private static (PushToHubRelay relay, FakePushEventBus bus, HubMocks mocks, FakeClusterLeader leader, RelayOutcomeSink sink)
        BuildSut(string group, bool isLeader = true, ILogger<PushToHubRelay>? logger = null)
    {
        var bus = new FakePushEventBus();
        var mocks = CreateMockHubContext(group);
        var leader = new FakeClusterLeader { IsLeader = isLeader };
        var sink = new RelayOutcomeSink(new RelayOutcomeSinkOptions
        {
            Capacity = 1_000,
            PodInstanceId = "test-pod",
        });
        var relay = new PushToHubRelay(
            bus,
            mocks.HubContext,
            leader,
            sink,
            logger ?? NullLogger<PushToHubRelay>.Instance);
        return (relay, bus, mocks, leader, sink);
    }

    private static PushEventMetadata MakeMetadata(string tenantId) =>
        new(TenantId: tenantId, UserId: null, OccurredAt: DateTimeOffset.UtcNow, CorrelationId: null);

    // Deterministic replacement for wall-clock sync fences: awaits the relay's most
    // recently started fire-and-forget send (or a completed Task for synchronous skip
    // outcomes) under a bounded timeout, instead of Task.Delay.
    private static Task WaitForDispatch(PushToHubRelay relay) =>
        relay.WaitForDispatchAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

    [Fact]
    public async Task StartAsync_ShouldForwardToTenantGroup_WhenConversationStateChangedEventIsPublished()
    {
        var (relay, bus, mocks, _, _) = BuildSut("tenant:acme");
        await relay.StartAsync(CancellationToken.None);

        var evt = new ConversationStateChangedEvent
        {
            ConversationId = "conv-1",
            PreviousState = "queued",
            NewState = "active",
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata("acme")
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        mocks.HubClients.Received(1).Group("tenant:acme");
        await mocks.GroupClient.Received(1).OnConversationStateChanged(
            Arg.Is<ConversationStatePayload>(p =>
                p.ConversationId == "conv-1" &&
                p.TenantId == "acme" &&
                p.PreviousState == "queued" &&
                p.NewState == "active"));

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldForwardToTenantGroup_WhenAgentStateChangedEventIsPublished()
    {
        var (relay, bus, mocks, _, _) = BuildSut("tenant:acme");
        await relay.StartAsync(CancellationToken.None);

        var evt = new AgentStateChangedEvent
        {
            AgentId = "agent-42",
            PreviousState = "ready",
            NewState = "paused",
            ReasonCode = "break",
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata("acme")
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        mocks.HubClients.Received(1).Group("tenant:acme");
        await mocks.GroupClient.Received(1).OnAgentStateChanged(
            Arg.Is<AgentStatePayload>(p =>
                p.AgentId == "agent-42" &&
                p.TenantId == "acme" &&
                p.ReasonCode == "break" &&
                p.PreviousState == "ready" &&
                p.NewState == "paused"));

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldForwardToSupervisorGroupViaTypedMethod_WhenCsatResponseRecordedEventIsPublished()
    {
        // csat-completion — the CSAT branch now delivers through the strongly-typed
        // IPlatformHubClient.OnCsatResponseRecorded (wire method name + payload shape unchanged).
        var (relay, bus, mocks, _, _) = BuildSut("supervisor:acme");
        await relay.StartAsync(CancellationToken.None);

        var capturedAt = DateTimeOffset.UtcNow;
        var evt = new Verbara.Platform.Core.CsatResponseRecordedEvent(
            TenantId: "acme",
            ResponseId: "resp-1",
            SurveyId: "srv-csat-v1",
            ConversationId: "conv-8f2a1c4e",
            Channel: "webchat",
            QueueName: "support-tier1",
            Rating: 5,
            Comment: "Fast and friendly, thanks!",
            CapturedAt: capturedAt);

        bus.Emit(evt);
        await WaitForDispatch(relay);

        mocks.HubClients.Received(1).Group("supervisor:acme");
        await mocks.GroupClient.Received(1).OnCsatResponseRecorded(
            Arg.Is<CsatResponseRecordedPayload>(p =>
                p.TenantId == "acme" &&
                p.Rating == 5 &&
                p.QueueName == "support-tier1" &&
                p.Channel == "webchat"));

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldDeliverVoiceChannelWithNullComment_WhenVoiceCsatRecorded()
    {
        // csat-completion — the payload channel set now includes 'voice', with a null comment (DTMF
        // carries no free text); the typed method delivers it to supervisor:{tenantId} unchanged.
        var (relay, bus, mocks, _, _) = BuildSut("supervisor:acme");
        await relay.StartAsync(CancellationToken.None);

        var evt = new Verbara.Platform.Core.CsatResponseRecordedEvent(
            TenantId: "acme",
            ResponseId: "resp-voice-1",
            SurveyId: "srv-csat-v1",
            ConversationId: "conv-8f2a1c4e",
            Channel: "voice",
            QueueName: "support-tier1",
            Rating: 4,
            Comment: null,
            CapturedAt: DateTimeOffset.UtcNow);

        bus.Emit(evt);
        await WaitForDispatch(relay);

        await mocks.GroupClient.Received(1).OnCsatResponseRecorded(
            Arg.Is<CsatResponseRecordedPayload>(p =>
                p.Channel == "voice" &&
                p.Rating == 4 &&
                p.QueueName == "support-tier1" &&
                p.Comment == null));

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipForward_WhenConversationEventTenantIdIsNullOrEmpty()
    {
        var (relay, bus, mocks, _, _) = BuildSut("tenant:");
        await relay.StartAsync(CancellationToken.None);

        var evt = new ConversationStateChangedEvent
        {
            ConversationId = "conv-2",
            PreviousState = "queued",
            NewState = "active",
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata(string.Empty)
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        await mocks.GroupClient.DidNotReceive().OnConversationStateChanged(Arg.Any<ConversationStatePayload>());

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipForward_WhenAgentEventTenantIdIsNullOrEmpty()
    {
        var (relay, bus, mocks, _, _) = BuildSut("tenant:");
        await relay.StartAsync(CancellationToken.None);

        var evt = new AgentStateChangedEvent
        {
            AgentId = "agent-99",
            PreviousState = "ready",
            NewState = "offline",
            ReasonCode = null,
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata(string.Empty)
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        await mocks.GroupClient.DidNotReceive().OnAgentStateChanged(Arg.Any<AgentStatePayload>());

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_ShouldDisposeSubscriptions_SoNoMoreForwardingAfterStop()
    {
        var (relay, bus, mocks, _, _) = BuildSut("tenant:tenant-x");
        await relay.StartAsync(CancellationToken.None);
        await relay.StopAsync(CancellationToken.None);

        var evt = new ConversationStateChangedEvent
        {
            ConversationId = "conv-3",
            PreviousState = "active",
            NewState = "closed",
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata("tenant-x")
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        await mocks.GroupClient.DidNotReceive().OnConversationStateChanged(Arg.Any<ConversationStatePayload>());
    }

    [Fact]
    public async Task StartAsync_ShouldForwardToAdminsPlatformGroup_WhenClusterNodeStateChangedEventIsPublished()
    {
        var (relay, bus, mocks, _, _) = BuildSut("admins:platform");
        await relay.StartAsync(CancellationToken.None);

        var evt = new ClusterNodeStateChangedEvent
        {
            NodeId = "node-1",
            PreviousState = "Healthy",
            NewState = "Degraded",
            ChangedAt = DateTimeOffset.UtcNow
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        mocks.HubClients.Received(1).Group("admins:platform");
        await mocks.GroupClient.Received(1).OnClusterNodeStateChanged(
            Arg.Is<ClusterNodeStatePayload>(p =>
                p.NodeId == "node-1" &&
                p.PreviousState == "Healthy" &&
                p.NewState == "Degraded"));

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipForward_WhenClusterEventNodeIdIsNullOrEmpty()
    {
        var (relay, bus, mocks, _, _) = BuildSut("admins:platform");
        await relay.StartAsync(CancellationToken.None);

        var evt = new ClusterNodeStateChangedEvent
        {
            NodeId = string.Empty,
            PreviousState = "Healthy",
            NewState = "Degraded",
            ChangedAt = DateTimeOffset.UtcNow
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        await mocks.GroupClient.DidNotReceive().OnClusterNodeStateChanged(Arg.Any<ClusterNodeStatePayload>());

        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // ADR-0022 Phase A.5 — leader-gating tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Forward_ShouldNotInvokeHubContext_WhenNotLeader()
    {
        var (relay, bus, mocks, _, _) = BuildSut("tenant:acme", isLeader: false);
        await relay.StartAsync(CancellationToken.None);

        var evt = new ConversationStateChangedEvent
        {
            ConversationId = "conv-leader-1",
            PreviousState = "queued",
            NewState = "active",
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata("acme")
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        mocks.HubClients.DidNotReceive().Group(Arg.Any<string>());
        await mocks.GroupClient.DidNotReceive().OnConversationStateChanged(Arg.Any<ConversationStatePayload>());

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Forward_ShouldInvokeHubContext_WhenLeader()
    {
        var (relay, bus, mocks, _, _) = BuildSut("tenant:acme", isLeader: true);
        await relay.StartAsync(CancellationToken.None);

        var evt = new ConversationStateChangedEvent
        {
            ConversationId = "conv-leader-2",
            PreviousState = "queued",
            NewState = "active",
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata("acme")
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        mocks.HubClients.Received(1).Group("tenant:acme");
        await mocks.GroupClient.Received(1).OnConversationStateChanged(Arg.Any<ConversationStatePayload>());

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Forward_ShouldRecover_AfterLeadershipRegained()
    {
        var (relay, bus, mocks, leader, _) = BuildSut("tenant:acme", isLeader: false);
        await relay.StartAsync(CancellationToken.None);

        var template = new ConversationStateChangedEvent
        {
            ConversationId = "conv-recover",
            PreviousState = "queued",
            NewState = "active",
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata("acme")
        };

        // 1) not-leader → must not invoke
        bus.Emit(template);
        await WaitForDispatch(relay);
        await mocks.GroupClient.DidNotReceive().OnConversationStateChanged(Arg.Any<ConversationStatePayload>());

        // 2) leadership regained → must invoke exactly once
        leader.IsLeader = true;
        bus.Emit(template);
        await WaitForDispatch(relay);
        await mocks.GroupClient.Received(1).OnConversationStateChanged(Arg.Any<ConversationStatePayload>());

        // 3) leadership lost again → no additional invocation
        leader.IsLeader = false;
        bus.Emit(template);
        await WaitForDispatch(relay);
        await mocks.GroupClient.Received(1).OnConversationStateChanged(Arg.Any<ConversationStatePayload>());

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Forward_ShouldEmitTraceLog_WhenSkippingDueToNotLeader()
    {
        var capturingLogger = new CapturingLogger<PushToHubRelay>();

        var (relay, bus, _, _, _) = BuildSut("tenant:acme", isLeader: false, logger: capturingLogger);
        await relay.StartAsync(CancellationToken.None);

        var evt = new ConversationStateChangedEvent
        {
            ConversationId = "conv-log",
            PreviousState = "queued",
            NewState = "active",
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata("acme")
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        // RealtimeLog.SkippedForwardNotLeader logs at Trace level with EventId 3001
        // and includes the resource in the formatted message. The event-type string
        // is whatever PushEvent.EventType resolves to (an opaque event-name override
        // on the concrete event type); we only check the message includes the value
        // we passed in to keep the assertion resilient to upstream renaming.
        capturingLogger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Trace &&
            e.EventId.Id == 3001 &&
            e.Message.Contains(evt.EventType, StringComparison.Ordinal) &&
            e.Message.Contains(RealtimeLeaderResources.Fanout, StringComparison.Ordinal));

        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // ADR-0022 Phase A.5 — outcome sink wiring (audit endpoint source of truth)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Forward_ShouldRecordForwardedOutcomeInSink_WhenLeader()
    {
        var (relay, bus, _, _, sink) = BuildSut("tenant:acme", isLeader: true);
        await relay.StartAsync(CancellationToken.None);

        var evt = new ConversationStateChangedEvent
        {
            ConversationId = "conv-sink-1",
            PreviousState = "queued",
            NewState = "active",
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata("acme")
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        var page = sink.Snapshot(since: null, limit: 100);
        page.PodInstanceId.Should().Be("test-pod");
        page.Entries.Should().ContainSingle(e =>
            e.EventType == evt.EventType &&
            e.TenantId == "acme" &&
            e.Outcome == RelayOutcomeKind.Forwarded &&
            e.IsLeaderAtTime &&
            e.Resource == RealtimeLeaderResources.Fanout &&
            e.PodInstanceId == "test-pod");

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Forward_ShouldRecordSkippedNotLeaderOutcomeInSink_WhenNotLeader()
    {
        var (relay, bus, _, _, sink) = BuildSut("tenant:acme", isLeader: false);
        await relay.StartAsync(CancellationToken.None);

        var evt = new ConversationStateChangedEvent
        {
            ConversationId = "conv-sink-2",
            PreviousState = "queued",
            NewState = "active",
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata("acme")
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        var page = sink.Snapshot(since: null, limit: 100);
        page.Entries.Should().ContainSingle(e =>
            e.EventType == evt.EventType &&
            e.TenantId == "acme" &&
            e.Outcome == RelayOutcomeKind.SkippedNotLeader &&
            !e.IsLeaderAtTime &&
            e.Resource == RealtimeLeaderResources.Fanout);

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Forward_ShouldRecordSkippedNullTenantOutcomeInSink_WhenTenantMissing()
    {
        var (relay, bus, _, _, sink) = BuildSut("tenant:acme", isLeader: true);
        await relay.StartAsync(CancellationToken.None);

        var evt = new ConversationStateChangedEvent
        {
            ConversationId = "conv-sink-3",
            PreviousState = "queued",
            NewState = "active",
            ChangedAt = DateTimeOffset.UtcNow,
            Metadata = MakeMetadata(string.Empty)
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        var page = sink.Snapshot(since: null, limit: 100);
        page.Entries.Should().ContainSingle(e =>
            e.EventType == evt.EventType &&
            string.IsNullOrEmpty(e.TenantId) &&
            e.Outcome == RelayOutcomeKind.SkippedNullTenant &&
            e.IsLeaderAtTime);

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Forward_ShouldRecordSkippedNullNodeIdOutcomeInSink_WhenClusterNodeIdMissing()
    {
        var (relay, bus, _, _, sink) = BuildSut("admins:platform", isLeader: true);
        await relay.StartAsync(CancellationToken.None);

        var evt = new ClusterNodeStateChangedEvent
        {
            NodeId = string.Empty,
            PreviousState = "Healthy",
            NewState = "Degraded",
            ChangedAt = DateTimeOffset.UtcNow,
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        var page = sink.Snapshot(since: null, limit: 100);
        page.Entries.Should().ContainSingle(e =>
            e.EventType == evt.EventType &&
            e.TenantId == null &&
            e.Outcome == RelayOutcomeKind.SkippedNullNodeId &&
            e.IsLeaderAtTime);

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Forward_ShouldRecordClusterForwardedOutcomeWithNullTenantId_WhenLeader()
    {
        var (relay, bus, _, _, sink) = BuildSut("admins:platform", isLeader: true);
        await relay.StartAsync(CancellationToken.None);

        var evt = new ClusterNodeStateChangedEvent
        {
            NodeId = "node-1",
            PreviousState = "Healthy",
            NewState = "Degraded",
            ChangedAt = DateTimeOffset.UtcNow,
        };

        bus.Emit(evt);
        await WaitForDispatch(relay);

        var page = sink.Snapshot(since: null, limit: 100);
        page.Entries.Should().ContainSingle(e =>
            e.EventType == evt.EventType &&
            e.TenantId == null &&
            e.Outcome == RelayOutcomeKind.Forwarded &&
            e.IsLeaderAtTime);

        await relay.StopAsync(CancellationToken.None);
    }
}
