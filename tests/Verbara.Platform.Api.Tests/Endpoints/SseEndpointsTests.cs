using System.Text.Json;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Notifications;
using FluentAssertions;

namespace Verbara.Platform.Api.Tests.Endpoints;

public sealed class SseEndpointsTests
{
    [Fact]
    public void IsDeliverableToUser_ShouldReturnTrue_WhenEventIsTenantScoped()
    {
        var evt = new ConversationAssignedEvent("tenant1", "conv1", "agent1", "Queue A", "whatsapp", "Alice");

        SseEndpoints.IsDeliverableToUser(evt, userId: "any-user").Should().BeTrue();
        SseEndpoints.IsDeliverableToUser(evt, userId: null).Should().BeTrue();
    }

    [Fact]
    public void IsDeliverableToUser_ShouldReturnTrue_WhenNotificationTargetsSameUser()
    {
        var evt = new NotificationEvent(
            "tenant1", "billing.invoice_due", DateTimeOffset.UtcNow,
            "notif1", "user1",
            NotificationCategory.Billing, NotificationSeverity.Warning,
            "Invoice due", "Your invoice is due soon.", null);

        SseEndpoints.IsDeliverableToUser(evt, userId: "user1").Should().BeTrue();
    }

    [Fact]
    public void IsDeliverableToUser_ShouldReturnFalse_WhenNotificationTargetsDifferentUser()
    {
        var evt = new NotificationEvent(
            "tenant1", "billing.invoice_due", DateTimeOffset.UtcNow,
            "notif1", "user1",
            NotificationCategory.Billing, NotificationSeverity.Warning,
            "Invoice due", "Your invoice is due soon.", null);

        SseEndpoints.IsDeliverableToUser(evt, userId: "user2").Should().BeFalse();
    }

    [Fact]
    public void IsDeliverableToUser_ShouldReturnFalse_WhenNotificationAndAnonymousConnection()
    {
        var evt = new NotificationEvent(
            "tenant1", "system.generic", DateTimeOffset.UtcNow,
            "notif1", "user1",
            NotificationCategory.System, NotificationSeverity.Info,
            "Hello", "Hi there.", null);

        SseEndpoints.IsDeliverableToUser(evt, userId: null).Should().BeFalse();
    }

    [Fact]
    public void WriteEventAsync_ShouldSerializeAllEventTypes_WhenUsingAotContext()
    {
        var now = DateTimeOffset.UtcNow;
        PlatformEvent[] events =
        [
            new ConversationAssignedEvent("t", "c1", "a1", "Q", "whatsapp", "Alice"),
            new ConversationMessageEvent("t", "c1", "m1", "agent", "hello"),
            new ConversationStateChangedEvent("t", "c1", "Active", "Closed"),
            new ConversationOfferedEvent("t", "c1", "a1", "q1", "WebChat"),
            new ConversationOfferExpiredEvent("t", "c1", "a1"),
            new ConversationAbandonedEvent("t", "c1", "q1"),
            new AgentStateChangedEvent("t", "a1", "Agent 1", "Available", "Busy"),
            new AgentCapacityChangedEvent("t", "a1", "voice", 1, 3, true),
            new CampaignStatusChangedEvent("t", 1, "Camp1", "Paused", "Running"),
            new CampaignMetricsUpdatedEvent("t", 1, 100, 50, 0.45, 0.02, 5),
            new CampaignDispositionSubmittedEvent("t", 1, "SALE", "a1"),
            new AgentAssistSuggestionEvent("t", "s1", "a1", "c1", "sug1", "Try this", "High", "kb", null),
            new AgentAssistSentimentEvent("t", "s1", "a1", "c1", "customer", 0.8f, "Positive", ["great"]),
            new AgentAssistComplianceAlertEvent("t", "s1", "a1", "c1", "r1", "forbidden phrase", "Critical"),
            new AgentAssistTranscriptEvent("t", "s1", "a1", "c1", "agent", "Hello, how can I help?", true),
            new VoiceScreenPopEvent("t", "conv1", "a1", "Voice", "contact1", "Ada Lovelace", "+15551234", "1780266205.0"),
            new NotificationEvent("t", "system.generic", now, "n1", "u1",
                NotificationCategory.System, NotificationSeverity.Info, "Title", "Body", null),
        ];

        foreach (var evt in events)
        {
            var json = JsonSerializer.Serialize(evt, evt.GetType(), ApiJsonContext.Default);
            json.Should().NotBeNullOrEmpty($"event type {evt.Type} must serialize via AOT context");
        }
    }

    [Fact]
    public void Serialize_ShouldEmitCamelCaseFields_ForVoiceScreenPopEvent()
    {
        // Locks the cross-repo SSE contract: the React client (use-sse.ts / conversation-store.ts)
        // reads camelCase data.agentId/conversationId/contactId — a PascalCase emit would silently
        // drop the screen-pop (the isForCurrentAgent filter would read undefined).
        var json = JsonSerializer.Serialize(
            new VoiceScreenPopEvent("t", "conv1", "agent1", "Voice", "contact1", "Ada Lovelace", "+15551234", "1780266205.0"),
            typeof(VoiceScreenPopEvent), ApiJsonContext.Default);

        json.Should().Contain("\"agentId\":\"agent1\"");
        json.Should().Contain("\"conversationId\":\"conv1\"");
        json.Should().Contain("\"contactId\":\"contact1\"");
        json.Should().Contain("\"channel\":\"Voice\"");
        json.Should().Contain("\"voiceLinkedId\":\"1780266205.0\"");
    }
}
