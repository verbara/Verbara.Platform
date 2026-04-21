using System.Reactive.Subjects;
using Asterisk.Platform.Api.Services;
using Asterisk.Sdk.Pro.Push.SignalR.Events;
using Asterisk.Sdk.Pro.Push.SignalR.Hubs;
using Asterisk.Sdk.Push.Bus;
using Asterisk.Sdk.Push.Events;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Asterisk.Platform.Api.Tests.Services;

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

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    private static (PushToHubRelay relay, FakePushEventBus bus, IClientProxy groupProxy)
        BuildSut(string group)
    {
        var bus = new FakePushEventBus();

        var groupProxy = Substitute.For<IClientProxy>();
        var hubClients = Substitute.For<IHubClients>();
        hubClients.Group(group).Returns(groupProxy);

        var hubContext = Substitute.For<IHubContext<PlatformHub>>();
        hubContext.Clients.Returns(hubClients);

        var relay = new PushToHubRelay(bus, hubContext, NullLogger<PushToHubRelay>.Instance);
        return (relay, bus, groupProxy);
    }

    private static PushEventMetadata MakeMetadata(string tenantId) =>
        new(TenantId: tenantId, UserId: null, OccurredAt: DateTimeOffset.UtcNow, CorrelationId: null);

    // -----------------------------------------------------------------------
    // ConversationStateChangedEvent
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_ShouldForwardToTenantGroup_WhenConversationStateChangedEventReceived()
    {
        var (relay, bus, groupProxy) = BuildSut("tenant:acme");
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
        await Task.Delay(100); // allow async fire-and-forget to complete

        await groupProxy.Received(1).SendCoreAsync(
            "OnConversationStateChanged",
            Arg.Is<object[]>(a => a.Length == 1),
            Arg.Any<CancellationToken>());

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldForwardToTenantGroup_WhenAgentStateChangedEventReceived()
    {
        var (relay, bus, groupProxy) = BuildSut("tenant:acme");
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

        await groupProxy.Received(1).SendCoreAsync(
            "OnAgentStateChanged",
            Arg.Is<object[]>(a => a.Length == 1),
            Arg.Any<CancellationToken>());

        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // Null/empty TenantId — SendCoreAsync must NOT be called
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_ShouldNotCallSendAsync_WhenConversationEventHasEmptyTenantId()
    {
        var (relay, bus, groupProxy) = BuildSut("tenant:");
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

        await groupProxy.DidNotReceive().SendCoreAsync(
            Arg.Any<string>(), Arg.Any<object[]>(), Arg.Any<CancellationToken>());

        await relay.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ShouldNotCallSendAsync_WhenAgentEventHasEmptyTenantId()
    {
        var (relay, bus, groupProxy) = BuildSut("tenant:");
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

        await groupProxy.DidNotReceive().SendCoreAsync(
            Arg.Any<string>(), Arg.Any<object[]>(), Arg.Any<CancellationToken>());

        await relay.StopAsync(CancellationToken.None);
    }

    // -----------------------------------------------------------------------
    // StopAsync disposes subscriptions — no SendCoreAsync after stop
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StopAsync_ShouldDisposeSubscriptions_SoNoMoreForwardingAfterStop()
    {
        var (relay, bus, groupProxy) = BuildSut("tenant:tenant-x");
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

        await groupProxy.DidNotReceive().SendCoreAsync(
            Arg.Any<string>(), Arg.Any<object[]>(), Arg.Any<CancellationToken>());
    }
}
