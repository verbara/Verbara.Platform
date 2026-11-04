using System.Text.Json;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Core.Push;

namespace Verbara.Platform.Api.Tests.Sse;

/// <summary>
/// contact-history-opens-by-assignment task 2.8 (design D17; spec <c>conversation-read-scope</c> "The live stream MUST
/// deliver conversation content only to its readers"; host fixtures <c>conversation-message-event.v1.json</c> and
/// <c>agentassist-events.v1.json</c>): the content predicate on the recipients tables of both fixtures, the guard that
/// classifies every <see cref="PlatformEvent"/>, the backplane guard, and the wire, which the audience never reaches.
/// </summary>
public sealed class ConversationContentDeliveryRuleTests
{
    private const string Tenant = "demo";
    private const string Owner = "a-owner";
    private const string Offered = "a-offered";
    private const string Other = "a-other";
    private const string NamedAgent = "a-9e8d7c6b5a493827";

    /// <summary>A subscriber of the recipients tables: whether it reads everything, and the Agent profile it handles as.</summary>
    public enum Subscriber
    {
        ReadsAll,
        OwnerAgent,
        OfferedAgent,
        NamedAgent,
        OtherAgent,
        HandleWithoutProfile,
        Anonymous,
    }

    private static bool Deliverable(PlatformEvent evt, Subscriber subscriber) => subscriber switch
    {
        Subscriber.Anonymous => ConversationContentDelivery.IsDeliverable(evt, reader: null),
        Subscriber.ReadsAll => ConversationContentDelivery.IsDeliverable(evt, readsAll: true, handlingAgentId: null),
        Subscriber.HandleWithoutProfile => ConversationContentDelivery.IsDeliverable(evt, readsAll: false, handlingAgentId: null),
        _ => ConversationContentDelivery.IsDeliverable(evt, readsAll: false, EntityId.From(subscriber switch
        {
            Subscriber.OwnerAgent => Owner,
            Subscriber.OfferedAgent => Offered,
            Subscriber.NamedAgent => NamedAgent,
            _ => Other,
        })),
    };

    private static ConversationMessageEvent Message(ConversationAudience audience) =>
        new(Tenant, "conv-1", "m-1", "Inbound", "WhatsApp") { Audience = audience };

    private static IEnumerable<PlatformEvent> AgentAssistEvents(string agentId) =>
    [
        new AgentAssistSuggestionEvent(Tenant, "as-1", agentId, "conv-1", "sg-01", "Offer a refund", "Important", "Keyword", null),
        new AgentAssistSentimentEvent(Tenant, "as-1", agentId, "conv-1", "Caller", -0.6f, "Frustrated", ["again"]),
        new AgentAssistComplianceAlertEvent(Tenant, "as-1", agentId, "conv-1", "cr-1", "full card number", "Warning"),
        new AgentAssistTranscriptEvent(Tenant, "as-1", agentId, "conv-1", "Caller", "third time I call", IsFinal: true),
    ];

    private static CsatResponseRecordedEvent Csat() =>
        new(Tenant, "r-1", "s-1", "conv-1", "WebChat", "Sales", 2, "the agent was rude", DateTimeOffset.UtcNow);

    // ═══ conversation-message-event.v1.json recipients ═══════════════════════════

    [Theory]
    [InlineData(Subscriber.ReadsAll, true)]
    [InlineData(Subscriber.OwnerAgent, true)]
    [InlineData(Subscriber.OfferedAgent, true)]
    [InlineData(Subscriber.OtherAgent, false)]
    [InlineData(Subscriber.HandleWithoutProfile, false)]
    [InlineData(Subscriber.Anonymous, false)]
    public void IsDeliverable_ShouldFollowTheMessageRecipientsTable_WhenTheMessageHasAnOwnerAndAnOffer(
        Subscriber subscriber, bool delivered)
    {
        Deliverable(Message(new ConversationAudience(Owner, Offered)), subscriber).Should().Be(delivered);
    }

    [Theory]
    [InlineData(Subscriber.ReadsAll, true)]
    [InlineData(Subscriber.OwnerAgent, false)]
    [InlineData(Subscriber.OfferedAgent, false)]
    [InlineData(Subscriber.OtherAgent, false)]
    [InlineData(Subscriber.HandleWithoutProfile, false)]
    [InlineData(Subscriber.Anonymous, false)]
    public void IsDeliverable_ShouldReachOnlyReaders_WhenTheMessageHasAnEmptyAudience(Subscriber subscriber, bool delivered)
    {
        Deliverable(Message(ConversationAudience.None), subscriber).Should().Be(delivered);
        Deliverable(new ConversationMessageEvent(Tenant, "conv-1", "m-1", "Inbound", "WhatsApp"), subscriber)
            .Should().Be(delivered, "a message published without an audience fails closed");
    }

    // ═══ agentassist-events.v1.json recipients ═══════════════════════════════════

    [Theory]
    [InlineData(Subscriber.ReadsAll, true)]
    [InlineData(Subscriber.NamedAgent, true)]
    [InlineData(Subscriber.OwnerAgent, false)] // the conversation's owner, when it is not the agent the event names
    [InlineData(Subscriber.OtherAgent, false)]
    [InlineData(Subscriber.HandleWithoutProfile, false)]
    [InlineData(Subscriber.Anonymous, false)]
    public void IsDeliverable_ShouldFollowTheAgentAssistRecipientsTable_WhenTheEventNamesAnAgent(
        Subscriber subscriber, bool delivered)
    {
        foreach (var evt in AgentAssistEvents(NamedAgent))
        {
            ((IConversationContentEvent)evt).Audience.Should().Be(new ConversationAudience(NamedAgent, null), evt.Type);
            Deliverable(evt, subscriber).Should().Be(delivered, evt.Type);
        }
    }

    [Theory]
    [InlineData(Subscriber.ReadsAll, true)]
    [InlineData(Subscriber.NamedAgent, false)]
    [InlineData(Subscriber.HandleWithoutProfile, false)]
    [InlineData(Subscriber.Anonymous, false)]
    public void IsDeliverable_ShouldReachOnlyReaders_WhenTheAgentAssistEventNamesNoAgent(Subscriber subscriber, bool delivered)
    {
        foreach (var evt in AgentAssistEvents(""))
        {
            ((IConversationContentEvent)evt).Audience.Should().Be(ConversationAudience.None, evt.Type);
            Deliverable(evt, subscriber).Should().Be(delivered, evt.Type);
        }
    }

    // ═══ csat.response_recorded: no agent audience ════════════════════════════════

    [Theory]
    [InlineData(Subscriber.ReadsAll, true)]
    [InlineData(Subscriber.OwnerAgent, false)]
    [InlineData(Subscriber.OtherAgent, false)]
    [InlineData(Subscriber.Anonymous, false)]
    public void IsDeliverable_ShouldReachOnlyReaders_WhenTheEventIsACsatResponse(Subscriber subscriber, bool delivered)
    {
        Csat().Audience.Should().Be(ConversationAudience.None);
        Deliverable(Csat(), subscriber).Should().Be(delivered);
    }

    // ═══ non-content events are untouched ═════════════════════════════════════════

    [Theory]
    [InlineData(Subscriber.ReadsAll)]
    [InlineData(Subscriber.OtherAgent)]
    [InlineData(Subscriber.HandleWithoutProfile)]
    [InlineData(Subscriber.Anonymous)]
    public void IsDeliverable_ShouldPassEveryNonContentEvent_WhateverTheSubscriber(Subscriber subscriber)
    {
        PlatformEvent[] events =
        [
            new ConversationOfferedEvent(Tenant, "conv-1", Offered, "q-1", "WhatsApp"),
            new ConversationStateChangedEvent(Tenant, "conv-1", "Queued", "Offered"),
            new ConversationAssignedEvent(Tenant, "conv-1", Owner, "Sales", "WhatsApp", "Ada"),
        ];

        foreach (var evt in events)
            Deliverable(evt, subscriber).Should().BeTrue(evt.Type);
    }

    // ═══ guard: every PlatformEvent is classified ═════════════════════════════════

    /// <summary>The content-bearing events of design D17. A new event fails the guard until it is placed in one list.</summary>
    private static readonly Type[] ContentEvents =
    [
        typeof(ConversationMessageEvent),
        typeof(AgentAssistSuggestionEvent),
        typeof(AgentAssistSentimentEvent),
        typeof(AgentAssistComplianceAlertEvent),
        typeof(AgentAssistTranscriptEvent),
        typeof(CsatResponseRecordedEvent),
    ];

    /// <summary>Events that carry ids, states, counters or names, never what a customer or an agent said (design D17, F7).</summary>
    private static readonly Type[] NonContentEvents =
    [
        typeof(AgentCapacityChangedEvent),
        typeof(AgentPendingStateChangedEvent),
        typeof(AgentStateChangedEvent),
        typeof(CampaignDispositionSubmittedEvent),
        typeof(CampaignMetricsUpdatedEvent),
        typeof(CampaignStatusChangedEvent),
        typeof(ConversationAbandonedEvent),
        typeof(ConversationAssignedEvent),
        typeof(ConversationOfferedEvent),
        typeof(ConversationOfferExpiredEvent),
        typeof(ConversationStateChangedEvent),
        typeof(NotificationEvent),
        typeof(TypificationSubmittedEvent),
        typeof(UserAccessRevokedEvent),
        typeof(VoiceAgentHangupEvent),
        typeof(VoiceScreenPopEvent),
    ];

    private static List<Type> PlatformEventTypes() =>
        [.. typeof(PlatformEvent).Assembly.GetTypes().Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(PlatformEvent)))];

    [Fact]
    public void EveryPlatformEvent_ShouldBeClassifiedAsContentOrNot_WhenItIsDeclared()
    {
        var types = PlatformEventTypes();

        types.Should().HaveCountGreaterThan(15, "the PlatformEvent hierarchy should be discovered");
        ContentEvents.Intersect(NonContentEvents).Should().BeEmpty("an event is content or not, never both");
        types.Should().BeEquivalentTo(ContentEvents.Concat(NonContentEvents),
            "every PlatformEvent must be classified for the live stream's content filter (design D17)");
        types.Where(t => t.IsAssignableTo(typeof(IConversationContentEvent))).Should().BeEquivalentTo(ContentEvents,
            "exactly the content events implement IConversationContentEvent, so the stream filters them");
    }

    // ═══ guard: a content event never crosses pods without its audience ══════════

    /// <summary>Whether the backplane payload of <paramref name="type"/> carries its audience.</summary>
    private static bool BackplaneCarriesTheAudience(Type type) =>
        PlatformPushJsonContext.Default.GetTypeInfo(type) is { } info
        && info.Properties.Any(p => p.Name == nameof(IConversationContentEvent.Audience));

    [Fact]
    public void EveryCrossPodContentEvent_ShouldCarryItsAudienceInTheBackplanePayload_WhenItIsCrossPod()
    {
        var crossPodContent = PlatformEventTypes()
            .Where(t => t.IsAssignableTo(typeof(IConversationContentEvent)) && t.IsAssignableTo(typeof(ICrossPodEvent)))
            .ToList();

        crossPodContent.Where(t => !BackplaneCarriesTheAudience(t)).Select(t => t.Name).Should().BeEmpty(
            "a content event that crosses pods without its audience would reach only the readers on other replicas " +
            "(design D17, Multi-replica): carry the audience in the PlatformPushJsonContext payload deliberately");

        // The guard bites: a content event's payload today has no audience, so marking one cross-pod would fail above.
        BackplaneCarriesTheAudience(typeof(ConversationMessageEvent)).Should().BeFalse();
    }

    // ═══ the wire is byte-identical (no audience member) ══════════════════════════

    private static string Wire(PlatformEvent evt) => JsonSerializer.Serialize(evt, evt.GetType(), ApiJsonContext.Default);

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    private const string FixtureConversation = "4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a";

    /// <summary>
    /// The SSE data line of each content event, serialized by <see cref="ApiJsonContext"/> on the branch base
    /// <c>c82d6385</c>, before the audience existed, from the values of the host fixtures' frames, whose <c>wire</c> strings
    /// are these bytes: the serializer writes the record's own members first and trims trailing zeros of the fraction.
    /// </summary>
    public static TheoryData<string, string> WireBeforeTheAudience() => new()
    {
        { "conversation.message", "{\"conversationId\":\"4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a\",\"messageId\":\"m-0003-b1c2d3e4f5a6b7c8\",\"sender\":\"Outbound\",\"text\":\"WhatsApp\",\"tenantId\":\"demo\",\"type\":\"conversation.message\",\"timestamp\":\"2026-11-03T10:42:07.512+00:00\",\"eventType\":\"conversation.message\",\"metadata\":{\"tenantId\":\"demo\",\"occurredAt\":\"2026-11-03T10:42:07.512+00:00\"}}" },
        { "agentassist.suggestion", "{\"sessionId\":\"as-5c4b3a29\",\"agentId\":\"a-9e8d7c6b5a493827\",\"conversationId\":\"4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a\",\"suggestionId\":\"sg-01\",\"text\":\"Offer to resend the replacement card tracking number.\",\"priority\":\"Important\",\"source\":\"Keyword\",\"tenantId\":\"demo\",\"type\":\"agentassist.suggestion\",\"timestamp\":\"2026-11-03T10:43:01.004+00:00\",\"eventType\":\"agentassist.suggestion\",\"metadata\":{\"tenantId\":\"demo\",\"occurredAt\":\"2026-11-03T10:43:01.004+00:00\"}}" },
        { "agentassist.sentiment", "{\"sessionId\":\"as-5c4b3a29\",\"agentId\":\"a-9e8d7c6b5a493827\",\"conversationId\":\"4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a\",\"speaker\":\"Caller\",\"score\":-0.62,\"label\":\"Frustrated\",\"triggerWords\":[\"again\",\"third time\"],\"tenantId\":\"demo\",\"type\":\"agentassist.sentiment\",\"timestamp\":\"2026-11-03T10:43:02.31+00:00\",\"eventType\":\"agentassist.sentiment\",\"metadata\":{\"tenantId\":\"demo\",\"occurredAt\":\"2026-11-03T10:43:02.31+00:00\"}}" },
        { "agentassist.compliance_alert", "{\"sessionId\":\"as-5c4b3a29\",\"agentId\":\"a-9e8d7c6b5a493827\",\"conversationId\":\"4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a\",\"ruleId\":\"cr-identity-check\",\"phrase\":\"full card number\",\"severity\":\"Warning\",\"tenantId\":\"demo\",\"type\":\"agentassist.compliance_alert\",\"timestamp\":\"2026-11-03T10:43:03.777+00:00\",\"eventType\":\"agentassist.compliance_alert\",\"metadata\":{\"tenantId\":\"demo\",\"occurredAt\":\"2026-11-03T10:43:03.777+00:00\"}}" },
        { "agentassist.transcript", "{\"sessionId\":\"as-5c4b3a29\",\"agentId\":\"a-9e8d7c6b5a493827\",\"conversationId\":\"4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a\",\"speaker\":\"Caller\",\"text\":\"This is the third time I am calling about the card.\",\"isFinal\":true,\"tenantId\":\"demo\",\"type\":\"agentassist.transcript\",\"timestamp\":\"2026-11-03T10:43:00.12+00:00\",\"eventType\":\"agentassist.transcript\",\"metadata\":{\"tenantId\":\"demo\",\"occurredAt\":\"2026-11-03T10:43:00.12+00:00\"}}" },
        { "csat.response_recorded", "{\"responseId\":\"r-1\",\"surveyId\":\"s-1\",\"conversationId\":\"4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a\",\"channel\":\"WebChat\",\"queueName\":\"Sales\",\"rating\":2,\"comment\":\"the agent was rude\",\"capturedAt\":\"2026-11-03T10:50:00.25+00:00\",\"tenantId\":\"demo\",\"type\":\"csat.response_recorded\",\"timestamp\":\"2026-11-03T10:50:00.25+00:00\",\"eventType\":\"csat.response_recorded\",\"metadata\":{\"tenantId\":\"demo\",\"occurredAt\":\"2026-11-03T10:50:00.25+00:00\"}}" },
    };

    private static PlatformEvent FixtureEvent(string type) => type switch
    {
        "conversation.message" => new ConversationMessageEvent(
                "demo", FixtureConversation, "m-0003-b1c2d3e4f5a6b7c8", "Outbound", "WhatsApp")
            { Timestamp = At("2026-11-03T10:42:07.512+00:00"), Audience = new ConversationAudience(Owner, Offered) },
        "agentassist.suggestion" => new AgentAssistSuggestionEvent("demo", "as-5c4b3a29", NamedAgent, FixtureConversation, "sg-01",
                "Offer to resend the replacement card tracking number.", "Important", "Keyword", null)
            { Timestamp = At("2026-11-03T10:43:01.004+00:00") },
        "agentassist.sentiment" => new AgentAssistSentimentEvent("demo", "as-5c4b3a29", NamedAgent, FixtureConversation, "Caller",
                -0.62f, "Frustrated", ["again", "third time"])
            { Timestamp = At("2026-11-03T10:43:02.310+00:00") },
        "agentassist.compliance_alert" => new AgentAssistComplianceAlertEvent("demo", "as-5c4b3a29", NamedAgent, FixtureConversation,
                "cr-identity-check", "full card number", "Warning")
            { Timestamp = At("2026-11-03T10:43:03.777+00:00") },
        "agentassist.transcript" => new AgentAssistTranscriptEvent("demo", "as-5c4b3a29", NamedAgent, FixtureConversation, "Caller",
                "This is the third time I am calling about the card.", IsFinal: true)
            { Timestamp = At("2026-11-03T10:43:00.120+00:00") },
        "csat.response_recorded" => new CsatResponseRecordedEvent(
            "demo", "r-1", "s-1", FixtureConversation, "WebChat", "Sales", 2, "the agent was rude", At("2026-11-03T10:50:00.250+00:00")),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    [Theory]
    [MemberData(nameof(WireBeforeTheAudience))]
    public void Serialize_ShouldWriteTheSameBytesAsBeforeTheAudience_WhenTheContentEventCarriesOne(string type, string before)
    {
        var evt = FixtureEvent(type);

        evt.Type.Should().Be(type);
        Wire(evt).Should().Be(before, "the audience is [JsonIgnore]: the SSE wire of every content event is unchanged");
        Wire(evt).Should().NotContainEquivalentOf("audience");
    }

    [Fact]
    public void Serialize_ShouldIgnoreTheStampedAudience_WhenTwoMessagesDifferOnlyByIt()
    {
        var stamped = (ConversationMessageEvent)FixtureEvent("conversation.message");

        Wire(stamped with { Audience = ConversationAudience.None }).Should().Be(Wire(stamped));
    }
}
