using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Core.Push;
using Verbara.Platform.Core.Webhooks;

namespace Verbara.Platform.Api.Tests.Sse;

/// <summary>
/// The bytes of the three serialized forms of a platform event: the SSE frame, the webhook envelope and the backplane
/// payload. <c>metadata</c> is a serialized member of every event, so <see cref="PlatformEvent.Metadata"/> keeping one
/// instance per event must not move or change it. The expected strings were captured by these tests on <c>835df6a7</c>,
/// where the property still built a new instance on every read.
/// </summary>
public sealed partial class PlatformEventWireTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 4, 10, 0, 0, 512, TimeSpan.Zero);

    private static PlatformEvent Event(string type) => type switch
    {
        "conversation.message" => new ConversationMessageEvent("acme", "c-1", "m-1", "Inbound", "Hello")
            { Timestamp = At, Audience = new ConversationAudience("a-1", "a-2") },
        "conversation.state_changed" => new ConversationStateChangedEvent("acme", "c-1", "Active", "Resolved") { Timestamp = At },
        "notification.created" => new NotificationEvent("acme", "ticket", At, "n-1", "user-1", NotificationCategory.System,
            NotificationSeverity.Info, "Title", "Body", null),
        "user.access_revoked" => new UserAccessRevokedEvent("acme", "user-2", "deleted") { Timestamp = At },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static TheoryData<string, string> SseData() => new()
    {
        { "conversation.message", "{\"conversationId\":\"c-1\",\"messageId\":\"m-1\",\"sender\":\"Inbound\",\"text\":\"Hello\",\"tenantId\":\"acme\",\"type\":\"conversation.message\",\"timestamp\":\"2026-11-04T10:00:00.512+00:00\",\"eventType\":\"conversation.message\",\"metadata\":{\"tenantId\":\"acme\",\"occurredAt\":\"2026-11-04T10:00:00.512+00:00\"}}" },
        { "conversation.state_changed", "{\"conversationId\":\"c-1\",\"oldState\":\"Active\",\"newState\":\"Resolved\",\"tenantId\":\"acme\",\"type\":\"conversation.state_changed\",\"timestamp\":\"2026-11-04T10:00:00.512+00:00\",\"eventType\":\"conversation.state_changed\",\"metadata\":{\"tenantId\":\"acme\",\"occurredAt\":\"2026-11-04T10:00:00.512+00:00\"}}" },
        { "notification.created", "{\"notificationType\":\"ticket\",\"notificationId\":\"n-1\",\"userId\":\"user-1\",\"category\":\"System\",\"severity\":\"Info\",\"title\":\"Title\",\"body\":\"Body\",\"metadata\":{\"tenantId\":\"acme\",\"userId\":\"user-1\",\"occurredAt\":\"2026-11-04T10:00:00.512+00:00\"},\"tenantId\":\"acme\",\"type\":\"notification.created\",\"timestamp\":\"2026-11-04T10:00:00.512+00:00\",\"eventType\":\"notification.created\"}" },
        { "user.access_revoked", "{\"userId\":\"user-2\",\"reason\":\"deleted\",\"metadata\":{\"tenantId\":\"acme\",\"userId\":\"user-2\",\"occurredAt\":\"2026-11-04T10:00:00.512+00:00\"},\"tenantId\":\"acme\",\"type\":\"user.access_revoked\",\"timestamp\":\"2026-11-04T10:00:00.512+00:00\",\"eventType\":\"user.access_revoked\"}" },
    };

    [Theory]
    [MemberData(nameof(SseData))]
    public async Task WriteEventAsync_ShouldWriteTheSameFrame_WhetherOrNotTheMetadataWasReadBefore(string type, string data)
    {
        var expected = $"event: {type}\ndata: {data}\n\n";

        (await SseFrame(Event(type), type)).Should().Be(expected);

        var read = Event(type);
        _ = read.Metadata;
        (await SseFrame(read, type)).Should().Be(expected, "the filter reads the metadata before the stream serializes the event");
    }

    private static async Task<string> SseFrame(PlatformEvent evt, string type)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await SseEndpoints.WriteEventAsync(context.Response, type, evt, CancellationToken.None);

        return Encoding.UTF8.GetString(body.ToArray());
    }

    public static TheoryData<string, string> WebhookBodies() => new()
    {
        { "conversation.message", "{\"id\":\"<id>\",\"type\":\"conversation.message\",\"tenantId\":\"acme\",\"timestamp\":\"2026-11-04T10:00:00.512+00:00\",\"data\":{\"conversationId\":\"c-1\",\"messageId\":\"m-1\",\"sender\":\"Inbound\",\"text\":\"Hello\",\"tenantId\":\"acme\",\"type\":\"conversation.message\",\"timestamp\":\"2026-11-04T10:00:00.512+00:00\",\"eventType\":\"conversation.message\",\"metadata\":{\"tenantId\":\"acme\",\"occurredAt\":\"2026-11-04T10:00:00.512+00:00\"}}}" },
        { "conversation.state_changed", "{\"id\":\"<id>\",\"type\":\"conversation.state_changed\",\"tenantId\":\"acme\",\"timestamp\":\"2026-11-04T10:00:00.512+00:00\",\"data\":{\"conversationId\":\"c-1\",\"oldState\":\"Active\",\"newState\":\"Resolved\",\"tenantId\":\"acme\",\"type\":\"conversation.state_changed\",\"timestamp\":\"2026-11-04T10:00:00.512+00:00\",\"eventType\":\"conversation.state_changed\",\"metadata\":{\"tenantId\":\"acme\",\"occurredAt\":\"2026-11-04T10:00:00.512+00:00\"}}}" },
        { "notification.created", "{\"id\":\"<id>\",\"type\":\"notification.created\",\"tenantId\":\"acme\",\"timestamp\":\"2026-11-04T10:00:00.512+00:00\",\"data\":{\"notificationType\":\"ticket\",\"notificationId\":\"n-1\",\"userId\":\"user-1\",\"category\":\"System\",\"severity\":\"Info\",\"title\":\"Title\",\"body\":\"Body\",\"metadata\":{\"tenantId\":\"acme\",\"userId\":\"user-1\",\"occurredAt\":\"2026-11-04T10:00:00.512+00:00\"},\"tenantId\":\"acme\",\"type\":\"notification.created\",\"timestamp\":\"2026-11-04T10:00:00.512+00:00\",\"eventType\":\"notification.created\"}}" },
    };

    [Theory]
    [MemberData(nameof(WebhookBodies))]
    public async Task HandleEvent_ShouldStoreTheSameWebhookBody_WhetherOrNotTheMetadataWasReadBefore(string type, string body)
    {
        (await WebhookBody(Event(type))).Should().Be(body);

        var read = Event(type);
        _ = read.Metadata;
        (await WebhookBody(read)).Should().Be(body);
    }

    private static async Task<string> WebhookBody(PlatformEvent evt)
    {
        using var bus = new PlatformEventBus();
        var subscriptions = Substitute.For<IWebhookSubscriptionStore>();
        var deliveries = Substitute.For<IWebhookDeliveryStore>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(At);
        subscriptions.GetActiveByEventTypeAsync(evt.TenantId, evt.Type, Arg.Any<CancellationToken>())
            .Returns([new WebhookSubscription("s-1", evt.TenantId, "Hook", "https://example.test/hook", "secret", [evt.Type], true, At, At)]);
        using var dispatcher = new WebhookDispatcher(
            bus, subscriptions, deliveries, clock, NullLogger<WebhookDispatcher>.Instance, Webhooks.MetricsWebhooksLeader.Holding());
        WebhookDelivery? stored = null;
        await deliveries.SaveAsync(Arg.Do<WebhookDelivery>(d => stored = d), Arg.Any<CancellationToken>());

        bus.Publish(evt);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await dispatcher.WaitForDispatchAsync(timeout.Token);

        stored.Should().NotBeNull();

        // The envelope's id is a new Guid for each event; it is the only member not fixed by the event.
        return EnvelopeId().Replace(stored!.Payload, "\"id\":\"<id>\"", 1);
    }

    [GeneratedRegex("\"id\":\"[0-9a-f]{32}\"", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnvelopeId();

    [Fact]
    public void Serialize_ShouldWriteTheSameBackplanePayload_WhetherOrNotTheMetadataWasReadBefore()
    {
        const string Expected = "{\"UserId\":\"user-2\",\"Reason\":\"deleted\",\"Metadata\":{\"TenantId\":\"acme\",\"UserId\":\"user-2\",\"OccurredAt\":\"2026-11-04T10:00:00.512+00:00\"},\"TenantId\":\"acme\",\"Type\":\"user.access_revoked\",\"Timestamp\":\"2026-11-04T10:00:00.512+00:00\",\"EventType\":\"user.access_revoked\"}";

        var unread = new UserAccessRevokedEvent("acme", "user-2", "deleted") { Timestamp = At };
        var read = new UserAccessRevokedEvent("acme", "user-2", "deleted") { Timestamp = At };
        _ = read.Metadata;

        JsonSerializer.Serialize(unread, PlatformPushJsonContext.Default.UserAccessRevokedEvent).Should().Be(Expected);
        JsonSerializer.Serialize(read, PlatformPushJsonContext.Default.UserAccessRevokedEvent).Should().Be(Expected);
    }
}
