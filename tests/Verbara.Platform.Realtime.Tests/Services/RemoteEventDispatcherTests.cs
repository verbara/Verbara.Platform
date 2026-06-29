using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;
using Verbara.Platform.Realtime.Services;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Verbara.Platform.Realtime.Tests.Services;

public sealed class RemoteEventDispatcherTests
{
    // Minimal IPushEventBus that exposes a Subject<PushEvent> so the test can
    // both inject RemotePushEvents AND observe the decoded typed events that
    // the dispatcher publishes back.
    private sealed class FakePushEventBus : IPushEventBus, IDisposable
    {
        private readonly Subject<PushEvent> _subject = new();

        public List<PushEvent> Published { get; } = new();

        public void Dispose() => _subject.Dispose();

        public void Emit(PushEvent evt) => _subject.OnNext(evt);

        public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default)
            where TEvent : PushEvent
        {
            Published.Add(pushEvent);
            _subject.OnNext(pushEvent);
            return ValueTask.CompletedTask;
        }

        public IObservable<PushEvent> AsObservable() => _subject;

        public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent =>
            (IObservable<TEvent>)System.Reactive.Linq.Observable.OfType<TEvent>(_subject);
    }

    private static RemotePushEvent MakeAgentEnvelope(AgentStateChangedEvent payload) =>
        MakeEnvelope(
            JsonSerializer.Serialize(payload, PlatformPushJsonContext.Default.AgentStateChangedEvent),
            payload.EventType);

    private static RemotePushEvent MakeConversationEnvelope(ConversationStateChangedEvent payload) =>
        MakeEnvelope(
            JsonSerializer.Serialize(payload, PlatformPushJsonContext.Default.ConversationStateChangedEvent),
            payload.EventType);

    private static RemotePushEvent MakeTypificationEnvelope(TypificationSubmittedEvent payload) =>
        MakeEnvelope(
            JsonSerializer.Serialize(payload, PlatformPushJsonContext.Default.TypificationSubmittedEvent),
            payload.EventType);

    private static RemotePushEvent MakeEnvelope(string json, string originalEventType) =>
        new(
            OriginalEventType: originalEventType,
            SourceNodeId: "node-test",
            RawPayload: Encoding.UTF8.GetBytes(json))
        {
            Metadata = new PushEventMetadata(
                TenantId: "acme",
                UserId: null,
                OccurredAt: DateTimeOffset.UtcNow,
                CorrelationId: null,
                TopicPath: null,
                TraceContext: null),
        };

    [Fact]
    public async Task Dispatch_ShouldRePublishTypedAgentStateChangedEvent_WhenEnvelopeRecognised()
    {
        using var bus = new FakePushEventBus();
        var dispatcher = new RemoteEventDispatcher(bus, NullLogger<RemoteEventDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        var typed = new AgentStateChangedEvent("acme", "agent-42", "Agent 42", "Offline", "Available");
        var envelope = MakeAgentEnvelope(typed);

        bus.Emit(envelope);

        bus.Published.Should().ContainSingle(e => e.GetType() == typeof(AgentStateChangedEvent));
        var decoded = bus.Published.OfType<AgentStateChangedEvent>().Single();
        decoded.AgentId.Should().Be("agent-42");
        decoded.AgentName.Should().Be("Agent 42");
        decoded.OldState.Should().Be("Offline");
        decoded.NewState.Should().Be("Available");
        decoded.TenantId.Should().Be("acme");

        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatch_ShouldRePublishTypedConversationStateChangedEvent_WhenEnvelopeRecognised()
    {
        using var bus = new FakePushEventBus();
        var dispatcher = new RemoteEventDispatcher(bus, NullLogger<RemoteEventDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        var typed = new ConversationStateChangedEvent("acme", "conv-1", "queued", "active");
        var envelope = MakeConversationEnvelope(typed);

        bus.Emit(envelope);

        bus.Published.Should().ContainSingle(e => e.GetType() == typeof(ConversationStateChangedEvent));
        var decoded = bus.Published.OfType<ConversationStateChangedEvent>().Single();
        decoded.ConversationId.Should().Be("conv-1");
        decoded.OldState.Should().Be("queued");
        decoded.NewState.Should().Be("active");
        decoded.TenantId.Should().Be("acme");

        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatch_ShouldRepublishTypificationSubmittedEvent_WhenOriginalEventTypeMatches()
    {
        using var bus = new FakePushEventBus();
        var dispatcher = new RemoteEventDispatcher(bus, NullLogger<RemoteEventDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        var typed = new TypificationSubmittedEvent(
            "acme", "conv-7", "schema-1", SchemaVersion: 3, "leaf-99", "agent-5");
        var envelope = MakeTypificationEnvelope(typed);

        bus.Emit(envelope);

        bus.Published.Should().ContainSingle(e => e.GetType() == typeof(TypificationSubmittedEvent));
        var decoded = bus.Published.OfType<TypificationSubmittedEvent>().Single();
        decoded.ConversationId.Should().Be("conv-7");
        decoded.SchemaId.Should().Be("schema-1");
        decoded.SchemaVersion.Should().Be(3);
        decoded.LeafNodeId.Should().Be("leaf-99");
        decoded.AgentId.Should().Be("agent-5");
        decoded.TenantId.Should().Be("acme");

        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatch_ShouldSkipSilently_WhenOriginalEventTypeIsUnknown()
    {
        using var bus = new FakePushEventBus();
        var dispatcher = new RemoteEventDispatcher(bus, NullLogger<RemoteEventDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        var envelope = new RemotePushEvent(
            OriginalEventType: "campaign.metrics_updated",
            SourceNodeId: "node-test",
            RawPayload: Encoding.UTF8.GetBytes("{}"));

        bus.Emit(envelope);

        // The envelope itself counts as a publish on Emit (from FakePushEventBus.Emit) —
        // no typed re-publish.
        bus.Published.Should().NotContain(e => e.GetType() == typeof(AgentStateChangedEvent) || e.GetType() == typeof(ConversationStateChangedEvent));

        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatch_ShouldSkipSilently_WhenRawPayloadIsEmpty()
    {
        using var bus = new FakePushEventBus();
        var dispatcher = new RemoteEventDispatcher(bus, NullLogger<RemoteEventDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        var envelope = new RemotePushEvent(
            OriginalEventType: "agent.state_changed",
            SourceNodeId: "node-test",
            RawPayload: Array.Empty<byte>());

        bus.Emit(envelope);

        bus.Published.Should().NotContain(e => e.GetType() == typeof(AgentStateChangedEvent));

        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Dispatch_ShouldCarryEnvelopeMetadataThroughToDecodedEvent()
    {
        using var bus = new FakePushEventBus();
        var dispatcher = new RemoteEventDispatcher(bus, NullLogger<RemoteEventDispatcher>.Instance);
        await dispatcher.StartAsync(CancellationToken.None);

        var typed = new AgentStateChangedEvent("acme", "agent-99", "Agent 99", "Available", "Busy");
        var envelope = MakeAgentEnvelope(typed);

        bus.Emit(envelope);

        var decoded = bus.Published.OfType<AgentStateChangedEvent>().Single();
        decoded.Metadata.Should().NotBeNull();
        decoded.Metadata!.TenantId.Should().Be("acme");

        await dispatcher.StopAsync(CancellationToken.None);
    }
}
