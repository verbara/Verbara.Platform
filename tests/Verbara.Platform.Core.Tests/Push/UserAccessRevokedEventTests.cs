using System.Text;
using System.Text.Json;
using Verbara.Platform.Core.Push;
using Verbara.Sdk.Push.Events;

namespace Verbara.Platform.Core.Tests.Push;

public sealed class UserAccessRevokedEventTests
{
    [Fact]
    public void Metadata_ShouldTargetTheRevokedUser_WhenTheEventIsCreated()
    {
        var evt = new UserAccessRevokedEvent("acme", "user-1", "suspended");

        evt.Metadata.TenantId.Should().Be("acme");
        evt.Metadata.UserId.Should().Be("user-1",
            because: "a user-targeted event never fans out as a tenant-wide broadcast");
        evt.EventType.Should().Be(UserAccessRevokedEvent.EventTypeName);
    }

    [Fact]
    public void TryRead_ShouldReturnTheEvent_WhenGivenTheTypedEvent()
    {
        var evt = new UserAccessRevokedEvent("acme", "user-1", "suspended");

        UserAccessRevokedEvent.TryRead(evt, out var read).Should().BeTrue();
        read.Should().BeSameAs(evt);
    }

    [Fact]
    public void TryRead_ShouldDecodeTheEvent_WhenGivenARemoteEnvelope()
    {
        var evt = new UserAccessRevokedEvent("acme", "user-1", "deactivated");
        var envelope = Envelope(
            UserAccessRevokedEvent.EventTypeName,
            JsonSerializer.Serialize(evt, PlatformPushJsonContext.Default.UserAccessRevokedEvent));

        UserAccessRevokedEvent.TryRead(envelope, out var read).Should().BeTrue();
        read!.TenantId.Should().Be("acme");
        read.UserId.Should().Be("user-1");
        read.Reason.Should().Be("deactivated");
    }

    [Fact]
    public void TryRead_ShouldReturnFalse_WhenRemoteEnvelopeHasEmptyPayload()
    {
        // A node with no PayloadSerializerOptions (Realtime) echoes re-published events with an
        // empty payload; the echo must be ignored, not crash the subscriber.
        var envelope = Envelope(UserAccessRevokedEvent.EventTypeName, json: "");

        UserAccessRevokedEvent.TryRead(envelope, out var read).Should().BeFalse();
        read.Should().BeNull();
    }

    [Fact]
    public void TryRead_ShouldReturnFalse_WhenRemoteEnvelopePayloadIsMalformed()
    {
        var envelope = Envelope(UserAccessRevokedEvent.EventTypeName, json: "{not json");

        UserAccessRevokedEvent.TryRead(envelope, out _).Should().BeFalse();
    }

    [Fact]
    public void TryRead_ShouldReturnFalse_WhenRemoteEnvelopeCarriesAnotherEventType()
    {
        var other = new AgentStateChangedEvent("acme", "agent-1", "Agent", "Offline", "Available");
        var envelope = Envelope(
            other.EventType,
            JsonSerializer.Serialize(other, PlatformPushJsonContext.Default.AgentStateChangedEvent));

        UserAccessRevokedEvent.TryRead(envelope, out _).Should().BeFalse();
    }

    [Fact]
    public void TryRead_ShouldReturnFalse_WhenTheEventIsOfAnUnrelatedType()
    {
        var other = new ConversationStateChangedEvent("acme", "conv-1", "queued", "active");

        UserAccessRevokedEvent.TryRead(other, out _).Should().BeFalse();
    }

    private static RemotePushEvent Envelope(string eventType, string json) =>
        new(OriginalEventType: eventType, SourceNodeId: "node-a", RawPayload: Encoding.UTF8.GetBytes(json))
        {
            Metadata = new PushEventMetadata("acme", null, DateTimeOffset.UtcNow, null, null, null),
        };
}
