using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Core.Push;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Platform.Core.Tests.Push;

public sealed class UserAccessRevocationListenerTests
{
    [Fact]
    public async Task Listener_ShouldAbortTheUsersConnections_WhenTheTypedEventIsPublishedOnThisNode()
    {
        using var bus = new FakePushEventBus();
        var registry = new LiveConnectionRegistry();
        var aborted = 0;
        using var connection = registry.Register("acme", "alice", () => aborted++);
        var listener = await StartListenerAsync(bus, registry);

        bus.Emit(new UserAccessRevokedEvent("acme", "alice", "suspended"));

        aborted.Should().Be(1);
        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Listener_ShouldAbortTheUsersConnections_WhenTheEventArrivesFromAnotherNode()
    {
        // Another Api replica handled the admin's request: this node only ever sees the envelope.
        using var bus = new FakePushEventBus();
        var registry = new LiveConnectionRegistry();
        var aborted = 0;
        using var connection = registry.Register("acme", "alice", () => aborted++);
        var listener = await StartListenerAsync(bus, registry);

        bus.Emit(Envelope(JsonSerializer.Serialize(
            new UserAccessRevokedEvent("acme", "alice", "deactivated"),
            PlatformPushJsonContext.Default.UserAccessRevokedEvent)));

        aborted.Should().Be(1);
        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Listener_ShouldLeaveOtherUsersConnected_WhenAUserIsRevoked()
    {
        using var bus = new FakePushEventBus();
        var registry = new LiveConnectionRegistry();
        var abortedBob = 0;
        using var bob = registry.Register("acme", "bob", () => abortedBob++);
        var listener = await StartListenerAsync(bus, registry);

        bus.Emit(new UserAccessRevokedEvent("acme", "alice", "suspended"));

        abortedBob.Should().Be(0);
        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Listener_ShouldAbortNothing_WhenTheEnvelopeHasNoPayload()
    {
        using var bus = new FakePushEventBus();
        var registry = new LiveConnectionRegistry();
        var aborted = 0;
        using var connection = registry.Register("acme", "alice", () => aborted++);
        var listener = await StartListenerAsync(bus, registry);

        var act = () => bus.Emit(Envelope(json: ""));

        act.Should().NotThrow();
        aborted.Should().Be(0);
        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Listener_ShouldKeepListening_WhenAnAbortFails()
    {
        using var bus = new FakePushEventBus();
        var registry = new LiveConnectionRegistry();
        using var broken = registry.Register("acme", "alice", () => throw new InvalidOperationException("transport failed"));
        var abortedBob = 0;
        using var bob = registry.Register("acme", "bob", () => abortedBob++);
        var listener = await StartListenerAsync(bus, registry);

        var act = () => bus.Emit(new UserAccessRevokedEvent("acme", "alice", "suspended"));

        act.Should().NotThrow(because: "an abort failure must never escape into the push bus's delivery loop");
        bus.Emit(new UserAccessRevokedEvent("acme", "bob", "suspended"));
        abortedBob.Should().Be(1, because: "the listener keeps revoking after a failure");
        await listener.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Listener_ShouldStopReacting_WhenStopped()
    {
        using var bus = new FakePushEventBus();
        var registry = new LiveConnectionRegistry();
        var aborted = 0;
        using var connection = registry.Register("acme", "alice", () => aborted++);
        var listener = await StartListenerAsync(bus, registry);
        await listener.StopAsync(CancellationToken.None);

        bus.Emit(new UserAccessRevokedEvent("acme", "alice", "suspended"));

        aborted.Should().Be(0);
    }

    private static async Task<UserAccessRevocationListener> StartListenerAsync(
        IPushEventBus bus, LiveConnectionRegistry registry)
    {
        var listener = new UserAccessRevocationListener(
            bus, registry, NullLogger<UserAccessRevocationListener>.Instance);
        await listener.StartAsync(CancellationToken.None);
        return listener;
    }

    private static RemotePushEvent Envelope(string json) =>
        new(UserAccessRevokedEvent.EventTypeName, "node-b", Encoding.UTF8.GetBytes(json))
        {
            Metadata = new PushEventMetadata("acme", "alice", DateTimeOffset.UtcNow, null, null, null),
        };

    private sealed class FakePushEventBus : IPushEventBus, IDisposable
    {
        private readonly Subject<PushEvent> _subject = new();

        public void Emit(PushEvent evt) => _subject.OnNext(evt);

        public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
            where TEvent : PushEvent
        {
            _subject.OnNext(pushEvent);
            return ValueTask.CompletedTask;
        }

        public IObservable<PushEvent> AsObservable() => _subject;

        public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent => _subject.OfType<TEvent>();

        public void Dispose() => _subject.Dispose();
    }
}
