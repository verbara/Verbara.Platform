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

    // Deterministic replacement for wall-clock sync fences: awaits the relay's most
    // recently started fire-and-forget send (or a completed Task for synchronous skip
    // outcomes) under a bounded timeout, instead of Task.Delay.
    private static Task WaitForDispatch(PushToHubRelay relay) =>
        relay.WaitForDispatchAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

    // -----------------------------------------------------------------------
    // Platform's own agent state (Verbara.Platform.Core.AgentStateChangedEvent, "agent.state_changed") is the only
    // source of OnAgentStateChanged frames (Pro/ADR-0026 point 4 removes the Pro agent bridge).
    // -----------------------------------------------------------------------

    private static Verbara.Platform.Core.AgentStateChangedEvent CoreAgentEvent(string tenantId, DateTimeOffset at) =>
        new Verbara.Platform.Core.AgentStateChangedEvent(tenantId, "agent-42", "Agent 42", "Available", "Break")
        {
            Timestamp = at,
        };

    [Fact]
    public async Task StartAsync_ShouldForwardCoreAgentStateToTenantGroup_WhenCoreAgentStateChangedEventIsPublished()
    {
        var (relay, bus, mocks, _, sink) = BuildSut("tenant:acme");
        await relay.StartAsync(CancellationToken.None);
        var at = new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);

        bus.Emit(CoreAgentEvent("acme", at));
        await WaitForDispatch(relay);

        mocks.HubClients.Received(1).Group("tenant:acme");
        await mocks.GroupClient.Received(1).OnAgentStateChanged(
            Arg.Is<AgentStatePayload>(p => p != null &&
                p.AgentId == "agent-42" &&
                p.TenantId == "acme" &&
                p.PreviousState == "Available" &&
                p.NewState == "Break" &&
                p.ReasonCode == null &&
                p.ChangedAt == at));
        sink.Snapshot(since: null, limit: 100).Entries.Should().ContainSingle(e =>
            e.EventType == "agent.state_changed" &&
            e.TenantId == "acme" &&
            e.Outcome == RelayOutcomeKind.Forwarded);

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipForward_WhenCoreAgentEventTenantIdIsEmpty()
    {
        var (relay, bus, mocks, _, sink) = BuildSut("tenant:");
        await relay.StartAsync(CancellationToken.None);

        bus.Emit(CoreAgentEvent(string.Empty, DateTimeOffset.UtcNow));
        await WaitForDispatch(relay);

        await mocks.GroupClient.DidNotReceive().OnAgentStateChanged(Arg.Any<AgentStatePayload>());
        sink.Snapshot(since: null, limit: 100).Entries.Should().ContainSingle(e =>
            e.EventType == "agent.state_changed" &&
            e.Outcome == RelayOutcomeKind.SkippedNullTenant);

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Forward_ShouldNotSendCoreAgentState_WhenNotLeader()
    {
        var (relay, bus, mocks, _, sink) = BuildSut("tenant:acme", isLeader: false);
        await relay.StartAsync(CancellationToken.None);

        bus.Emit(CoreAgentEvent("acme", DateTimeOffset.UtcNow));
        await WaitForDispatch(relay);

        mocks.HubClients.DidNotReceive().Group(Arg.Any<string>());
        await mocks.GroupClient.DidNotReceive().OnAgentStateChanged(Arg.Any<AgentStatePayload>());
        sink.Snapshot(since: null, limit: 100).Entries.Should().ContainSingle(e =>
            e.EventType == "agent.state_changed" &&
            e.TenantId == "acme" &&
            e.Outcome == RelayOutcomeKind.SkippedNotLeader);

        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Platform's own conversation state (Verbara.Platform.Core.ConversationStateChangedEvent,
    // "conversation.state_changed") is the only source of OnConversationStateChanged frames: the Pro
    // conversation bridge is not wired (owner Q8 = A, 2026-11-18).
    // -----------------------------------------------------------------------

    private static Verbara.Platform.Core.ConversationStateChangedEvent CoreConversationEvent(
        string tenantId, string conversationId, DateTimeOffset at) =>
        new Verbara.Platform.Core.ConversationStateChangedEvent(tenantId, conversationId, "Queued", "Active")
        {
            Timestamp = at,
        };

    [Fact]
    public async Task StartAsync_ShouldForwardCoreConversationStateToTenantGroup_WhenCoreConversationStateChangedEventIsPublished()
    {
        var (relay, bus, mocks, _, sink) = BuildSut("tenant:acme");
        await relay.StartAsync(CancellationToken.None);
        var at = new DateTimeOffset(2026, 11, 20, 9, 45, 0, TimeSpan.Zero);

        bus.Emit(CoreConversationEvent("acme", "conv-core-1", at));
        await WaitForDispatch(relay);

        mocks.HubClients.Received(1).Group("tenant:acme");
        await mocks.GroupClient.Received(1).OnConversationStateChanged(
            Arg.Is<ConversationStatePayload>(p => p != null &&
                p.ConversationId == "conv-core-1" &&
                p.TenantId == "acme" &&
                p.PreviousState == "Queued" &&
                p.NewState == "Active" &&
                p.ChangedAt == at));
        sink.Snapshot(since: null, limit: 100).Entries.Should().ContainSingle(e =>
            e.EventType == "conversation.state_changed" &&
            e.TenantId == "acme" &&
            e.Outcome == RelayOutcomeKind.Forwarded);

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipForward_WhenCoreConversationEventTenantIdIsEmpty()
    {
        var (relay, bus, mocks, _, sink) = BuildSut("tenant:");
        await relay.StartAsync(CancellationToken.None);

        bus.Emit(CoreConversationEvent(string.Empty, "conv-core-2", DateTimeOffset.UtcNow));
        await WaitForDispatch(relay);

        await mocks.GroupClient.DidNotReceive().OnConversationStateChanged(Arg.Any<ConversationStatePayload>());
        sink.Snapshot(since: null, limit: 100).Entries.Should().ContainSingle(e =>
            e.EventType == "conversation.state_changed" &&
            e.Outcome == RelayOutcomeKind.SkippedNullTenant);

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Forward_ShouldNotSendCoreConversationState_WhenNotLeader()
    {
        var (relay, bus, mocks, _, sink) = BuildSut("tenant:acme", isLeader: false);
        await relay.StartAsync(CancellationToken.None);

        bus.Emit(CoreConversationEvent("acme", "conv-core-3", DateTimeOffset.UtcNow));
        await WaitForDispatch(relay);

        mocks.HubClients.DidNotReceive().Group(Arg.Any<string>());
        await mocks.GroupClient.DidNotReceive().OnConversationStateChanged(Arg.Any<ConversationStatePayload>());
        sink.Snapshot(since: null, limit: 100).Entries.Should().ContainSingle(e =>
            e.EventType == "conversation.state_changed" &&
            e.TenantId == "acme" &&
            e.Outcome == RelayOutcomeKind.SkippedNotLeader);

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
            Arg.Is<CsatResponseRecordedPayload>(p => p != null &&
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
            Arg.Is<CsatResponseRecordedPayload>(p => p != null &&
                p.Channel == "voice" &&
                p.Rating == 4 &&
                p.QueueName == "support-tier1" &&
                p.Comment == null));

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_ShouldDisposeSubscriptions_SoNoMoreForwardingAfterStop()
    {
        var (relay, bus, mocks, _, _) = BuildSut("tenant:tenant-x");
        await relay.StartAsync(CancellationToken.None);
        await relay.StopAsync(CancellationToken.None);

        var evt = new Verbara.Platform.Core.ConversationStateChangedEvent("tenant-x", "conv-3", "active", "closed");

        bus.Emit(evt);
        await WaitForDispatch(relay);

        await mocks.GroupClient.DidNotReceive().OnConversationStateChanged(Arg.Any<ConversationStatePayload>());
    }

    // -----------------------------------------------------------------------
    // platform-drops-the-cluster-bridge — the relay has no cluster path. The Pro event type still ships in the
    // pinned Pro package, so a cluster event on the bus must reach no hub group and leave no relay outcome.
    // A Core agent event after it is the positive control: the relay is started and does forward.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_ShouldSendNoClusterFrameAndRecordNoOutcome_WhenClusterNodeStateChangedEventIsPublished()
    {
        var (relay, bus, mocks, _, sink) = BuildSut("admins:platform", isLeader: true);
        await relay.StartAsync(CancellationToken.None);

        var clusterEvent = new ClusterNodeStateChangedEvent
        {
            NodeId = "node-1",
            PreviousState = "Healthy",
            NewState = "Draining",
            ChangedAt = DateTimeOffset.UtcNow,
        };
        bus.Emit(clusterEvent);
        await WaitForDispatch(relay);

        bus.Emit(CoreAgentEvent("acme", DateTimeOffset.UtcNow));
        await WaitForDispatch(relay);

        mocks.HubClients.DidNotReceive().Group("admins:platform");
        await mocks.GroupClient.DidNotReceive().OnClusterNodeStateChanged(Arg.Any<ClusterNodeStatePayload>());
        mocks.HubClients.Received(1).Group("tenant:acme");
        mocks.HubClients.ReceivedCalls().Should().ContainSingle();
        var entries = sink.Snapshot(since: null, limit: 100).Entries;
        entries.Should().NotContain(e => e.EventType == clusterEvent.EventType);
        entries.Should().ContainSingle(e =>
            e.EventType == "agent.state_changed" &&
            e.TenantId == "acme" &&
            e.Outcome == RelayOutcomeKind.Forwarded);

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

        var evt = new Verbara.Platform.Core.ConversationStateChangedEvent("acme", "conv-leader-1", "queued", "active");

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

        var evt = new Verbara.Platform.Core.ConversationStateChangedEvent("acme", "conv-leader-2", "queued", "active");

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

        var template = new Verbara.Platform.Core.ConversationStateChangedEvent("acme", "conv-recover", "queued", "active");

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

        var evt = new Verbara.Platform.Core.ConversationStateChangedEvent("acme", "conv-log", "queued", "active");

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
            e.Message.Contains(evt.Type, StringComparison.Ordinal) &&
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

        var evt = new Verbara.Platform.Core.ConversationStateChangedEvent("acme", "conv-sink-1", "queued", "active");

        bus.Emit(evt);
        await WaitForDispatch(relay);

        var page = sink.Snapshot(since: null, limit: 100);
        page.PodInstanceId.Should().Be("test-pod");
        page.Entries.Should().ContainSingle(e =>
            e.EventType == "conversation.state_changed" &&
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

        var evt = new Verbara.Platform.Core.ConversationStateChangedEvent("acme", "conv-sink-2", "queued", "active");

        bus.Emit(evt);
        await WaitForDispatch(relay);

        var page = sink.Snapshot(since: null, limit: 100);
        page.Entries.Should().ContainSingle(e =>
            e.EventType == "conversation.state_changed" &&
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

        var evt = new Verbara.Platform.Core.ConversationStateChangedEvent(string.Empty, "conv-sink-3", "queued", "active");

        bus.Emit(evt);
        await WaitForDispatch(relay);

        var page = sink.Snapshot(since: null, limit: 100);
        page.Entries.Should().ContainSingle(e =>
            e.EventType == "conversation.state_changed" &&
            string.IsNullOrEmpty(e.TenantId) &&
            e.Outcome == RelayOutcomeKind.SkippedNullTenant &&
            e.IsLeaderAtTime);

        await relay.StopAsync(CancellationToken.None);
    }
}
