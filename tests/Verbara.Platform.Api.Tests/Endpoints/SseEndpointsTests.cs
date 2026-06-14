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
    public void AllPlatformEvents_ShouldResolveInApiJsonContext()
    {
        // SSE serializes events by RUNTIME type (SseEndpoints.cs:195:
        // JsonSerializer.Serialize(data, data.GetType(), ApiJsonContext.Default)), so a missing
        // [JsonSerializable] registration is a RUNTIME crash the AOT analyzer cannot see. Enumerate
        // the closed PlatformEvent hierarchy (reflection is fine — tests are not AOT) and assert
        // each resolves. This replaces a hardcoded array that had itself gone stale (W4-C1 class).
        var eventTypes = typeof(PlatformEvent).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(PlatformEvent)))
            .ToList();

        eventTypes.Should().HaveCountGreaterThan(15, "the PlatformEvent hierarchy should be discovered");

        var unregistered = eventTypes
            .Where(t => ApiJsonContext.Default.GetTypeInfo(t) is null)
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        unregistered.Should().BeEmpty(
            "every PlatformEvent must be in ApiJsonContext for AOT SSE serialization");
    }

    [Fact]
    public void Serialize_ShouldEmitCamelCaseFields_ForVoiceScreenPopEvent()
    {
        // Locks the cross-repo SSE contract: the React client (use-sse.ts / conversation-store.ts)
        // reads camelCase data.agentId/conversationId/contactId — a PascalCase emit would silently
        // drop the screen-pop (the isForCurrentAgent filter would read undefined).
        var json = JsonSerializer.Serialize(
            new VoiceScreenPopEvent("t", "conv1", "agent1", "Voice", "contact1", "Ada Lovelace", "+15551234", "1780266205.0", "Sales", true),
            typeof(VoiceScreenPopEvent), ApiJsonContext.Default);

        json.Should().Contain("\"agentId\":\"agent1\"");
        json.Should().Contain("\"conversationId\":\"conv1\"");
        json.Should().Contain("\"contactId\":\"contact1\"");
        json.Should().Contain("\"channel\":\"Voice\"");
        json.Should().Contain("\"voiceLinkedId\":\"1780266205.0\"");
        // 3B.2b: the queue + its auto-answer default ride the screen-pop so the client computes
        // the effective auto-answer (agent override ?? queue default) without a refetch.
        json.Should().Contain("\"queueName\":\"Sales\"");
        json.Should().Contain("\"queueAutoAnswerDefault\":true");
    }
}
