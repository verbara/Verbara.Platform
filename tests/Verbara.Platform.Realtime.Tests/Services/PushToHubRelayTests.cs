using System.Reactive.Subjects;
using Verbara.Platform.Realtime.Services;
using Verbara.Sdk.Pro.Push.SignalR.Events;
using Verbara.Sdk.Pro.Push.SignalR.Hubs;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Realtime.Tests.Services;

public sealed class PushToHubRelayTests
{
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

        var hubClients = Substitute.For<IHubClients<IPlatformHubClient>>();
        hubClients.Group(group).Returns(groupClient);

        var hubContext = Substitute.For<IHubContext<PlatformHub, IPlatformHubClient>>();
        hubContext.Clients.Returns(hubClients);

        return new HubMocks(hubContext, hubClients, groupClient);
    }

    private static (PushToHubRelay relay, FakePushEventBus bus, HubMocks mocks)
        BuildSut(string group)
    {
        var bus = new FakePushEventBus();
        var mocks = CreateMockHubContext(group);
        var relay = new PushToHubRelay(bus, mocks.HubContext, NullLogger<PushToHubRelay>.Instance);
        return (relay, bus, mocks);
    }

    private static PushEventMetadata MakeMetadata(string tenantId) =>
        new(TenantId: tenantId, UserId: null, OccurredAt: DateTimeOffset.UtcNow, CorrelationId: null);

    [Fact]
    public async Task StartAsync_ShouldForwardToTenantGroup_WhenConversationStateChangedEventIsPublished()
    {
        var (relay, bus, mocks) = BuildSut("tenant:acme");
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
        await Task.Delay(100);

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
        var (relay, bus, mocks) = BuildSut("tenant:acme");
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
        await Task.Delay(100);

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
    public async Task StartAsync_ShouldSkipForward_WhenConversationEventTenantIdIsNullOrEmpty()
    {
        var (relay, bus, mocks) = BuildSut("tenant:");
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
        await Task.Delay(100);

        await mocks.GroupClient.DidNotReceive().OnConversationStateChanged(Arg.Any<ConversationStatePayload>());

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldSkipForward_WhenAgentEventTenantIdIsNullOrEmpty()
    {
        var (relay, bus, mocks) = BuildSut("tenant:");
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
        await Task.Delay(100);

        await mocks.GroupClient.DidNotReceive().OnAgentStateChanged(Arg.Any<AgentStatePayload>());

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_ShouldDisposeSubscriptions_SoNoMoreForwardingAfterStop()
    {
        var (relay, bus, mocks) = BuildSut("tenant:tenant-x");
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
        await Task.Delay(100);

        await mocks.GroupClient.DidNotReceive().OnConversationStateChanged(Arg.Any<ConversationStatePayload>());
    }

    [Fact]
    public async Task StartAsync_ShouldForwardToAdminsPlatformGroup_WhenClusterNodeStateChangedEventIsPublished()
    {
        var (relay, bus, mocks) = BuildSut("admins:platform");
        await relay.StartAsync(CancellationToken.None);

        var evt = new ClusterNodeStateChangedEvent
        {
            NodeId = "node-1",
            PreviousState = "Healthy",
            NewState = "Degraded",
            ChangedAt = DateTimeOffset.UtcNow
        };

        bus.Emit(evt);
        await Task.Delay(100);

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
        var (relay, bus, mocks) = BuildSut("admins:platform");
        await relay.StartAsync(CancellationToken.None);

        var evt = new ClusterNodeStateChangedEvent
        {
            NodeId = string.Empty,
            PreviousState = "Healthy",
            NewState = "Degraded",
            ChangedAt = DateTimeOffset.UtcNow
        };

        bus.Emit(evt);
        await Task.Delay(100);

        await mocks.GroupClient.DidNotReceive().OnClusterNodeStateChanged(Arg.Any<ClusterNodeStatePayload>());

        await relay.StopAsync(CancellationToken.None);
    }
}
