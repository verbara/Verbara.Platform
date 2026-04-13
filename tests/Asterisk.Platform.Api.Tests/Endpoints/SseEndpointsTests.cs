using Asterisk.Platform.Api.Endpoints;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Notifications;
using FluentAssertions;

namespace Asterisk.Platform.Api.Tests.Endpoints;

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
}
