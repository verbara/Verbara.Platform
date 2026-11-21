using System.Reflection;
using System.Text.Json;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;
using Verbara.Sdk.Push.Events;
using ProEvents = Verbara.Sdk.Pro.Push.SignalR.Events;

namespace Verbara.Platform.Api.Tests.Push;

/// <summary>
/// The push backplane relay serializes the payload of every event on Platform.Api's push bus with
/// <c>JsonSerializer.Serialize(event, event.GetType(), options)</c>, using the options
/// <see cref="PushRelayPayloadSerialization.CreateOptions"/> builds. A type the resolver does not know throws
/// <see cref="NotSupportedException"/> there, the relay counts the event as dropped (reason
/// <c>serialization_error</c>) and sends it to the other replicas with an empty payload.
/// </summary>
public sealed class PushRelayPayloadSerializationTests
{
    public static TheoryData<PushEvent> RelayedEvents() => new()
    {
        new VoiceScreenPopEvent(
            TenantId: "acme",
            ConversationId: "conv-1",
            AgentId: "agent-1",
            Channel: "voice",
            ContactId: "contact-1",
            ContactName: "Jane Doe",
            CallerNumber: "+15550100",
            VoiceLinkedId: "1700000000.1",
            QueueName: "sales",
            QueueAutoAnswerDefault: true),
        new VoiceAgentHangupEvent(
            TenantId: "acme",
            ConversationId: "conv-1",
            QueueName: "sales",
            Abnormal: false,
            HangupAt: new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero)),
        new ProEvents.ClusterNodeStateChangedEvent
        {
            NodeId = "node-a",
            PreviousState = "Healthy",
            NewState = "Draining",
            ChangedAt = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero),
        },
    };

    [Theory]
    [MemberData(nameof(RelayedEvents))]
    public void CreateOptions_ShouldSerializeTheEventPayload_WhenTheRelayCarriesIt(PushEvent pushEvent)
    {
        var options = PushRelayPayloadSerialization.CreateOptions();

        // The exact call RedisEventRelay.SerializePayload makes.
        var json = JsonSerializer.Serialize(pushEvent, pushEvent.GetType(), options);

        json.Should().NotBeNullOrWhiteSpace();
        json.Should().StartWith("{");
    }

    [Fact]
    public void CreateOptions_ShouldResolveEveryEventTypeThePushBusCanCarry()
    {
        var options = PushRelayPayloadSerialization.CreateOptions();

        var unresolved = new List<string>();
        foreach (var type in PushEventTypesReachableFromTheApi())
        {
            try
            {
                options.GetTypeInfo(type);
            }
            catch (NotSupportedException)
            {
                unresolved.Add(type.FullName!);
            }
        }

        string.Join(", ", unresolved.Order(StringComparer.Ordinal)).Should().BeEmpty(
            "every event that can reach Platform.Api's push bus is forwarded by the backplane relay, which serializes " +
            "its payload with these options; register the missing type in PushRelayPayloadJsonContext");
    }

    [Fact]
    public void CreateOptions_ShouldSerializeCrossPodEventsExactlyAsTheReceivingSideDecodesThem()
    {
        var options = PushRelayPayloadSerialization.CreateOptions();
        var evt = new TypificationSubmittedEvent(
            TenantId: "acme",
            ConversationId: "conv-7",
            SchemaId: "schema-1",
            SchemaVersion: 3,
            LeafNodeId: "leaf-99",
            AgentId: "agent-5");

        var json = JsonSerializer.Serialize(evt, evt.GetType(), options);
        var decoded = JsonSerializer.Deserialize(json, PlatformPushJsonContext.Default.TypificationSubmittedEvent);

        decoded.Should().NotBeNull();
        decoded!.ConversationId.Should().Be("conv-7");
        decoded.SchemaVersion.Should().Be(3);
        decoded.AgentId.Should().Be("agent-5");
    }

    [Fact]
    public void PushEventTypesReachableFromTheApi_ShouldIncludeTheKnownProducers()
    {
        // Guards the enumeration itself: if it found nothing, the resolve test above would pass vacuously.
        var types = PushEventTypesReachableFromTheApi();

        types.Should().Contain(typeof(VoiceScreenPopEvent));
        types.Should().Contain(typeof(VoiceAgentHangupEvent));
        types.Should().Contain(typeof(AgentStateChangedEvent));
        types.Should().Contain(typeof(ProEvents.ClusterNodeStateChangedEvent));
        types.Should().NotContain(typeof(RemotePushEvent));
    }

    /// <summary>
    /// Pro event types that still ship in the pinned package but that nothing in Platform.Api publishes any more, so
    /// they never reach the bus. Named by string, not <c>typeof</c>, so this compiles after Pro deletes them.
    /// The Pro conversation-state event's only publisher is <c>ConversationStatePushBridge</c>, which Platform no
    /// longer wires (owner Q8 = A, 2026-11-18); its entry can go when Pro removes that bridge. (The Pro agent-state
    /// event left with its bridge in Pro <c>2.31.0-pro</c>, and its entry with it.)
    /// </summary>
    private static readonly HashSet<string> UnpublishedProEventTypes = new(StringComparer.Ordinal)
    {
        "Verbara.Sdk.Pro.Push.SignalR.Events.ConversationStateChangedEvent",
    };

    /// <summary>
    /// Every concrete, public <see cref="PushEvent"/> type in the Verbara assemblies Platform.Api loads, except
    /// <see cref="RemotePushEvent"/>, which the relay never forwards, and <see cref="UnpublishedProEventTypes"/>.
    /// Test-only reflection; nothing here ships.
    /// </summary>
    private static HashSet<Type> PushEventTypesReachableFromTheApi()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<Assembly>();
        pending.Enqueue(typeof(PushRelayPayloadSerialization).Assembly);
        var types = new HashSet<Type>();

        while (pending.Count > 0)
        {
            var assembly = pending.Dequeue();
            if (!seen.Add(assembly.GetName().Name!))
            {
                continue;
            }

            types.UnionWith(assembly.GetTypes().Where(t =>
                t is { IsAbstract: false, IsGenericTypeDefinition: false }
                && (t.IsPublic || t.IsNestedPublic)
                && t.IsAssignableTo(typeof(PushEvent))
                && t != typeof(RemotePushEvent)
                && !UnpublishedProEventTypes.Contains(t.FullName!)));

            var verbaraReferences = assembly.GetReferencedAssemblies().Where(r =>
                r.Name is { } name
                && name.StartsWith("Verbara.", StringComparison.Ordinal)
                && !seen.Contains(name));
            foreach (var reference in verbaraReferences)
            {
                pending.Enqueue(Assembly.Load(reference));
            }
        }

        return types;
    }
}
