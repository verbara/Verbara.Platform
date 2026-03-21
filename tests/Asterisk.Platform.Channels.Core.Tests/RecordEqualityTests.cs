using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;

namespace Asterisk.Platform.Channels.Core.Tests;

public class RecordEqualityTests
{
    [Fact]
    public void SendResult_ShouldBeEqual_WhenAllPropertiesMatch()
    {
        var a = new SendResult(true, "ext-123", null, null);
        var b = new SendResult(true, "ext-123", null, null);

        a.Should().Be(b);
    }

    [Fact]
    public void DeliveryStatusUpdate_ShouldBeEqual_WhenAllPropertiesMatch()
    {
        var ts = DateTimeOffset.UtcNow;
        var a = new DeliveryStatusUpdate("ext-123", MessageDeliveryStatus.Delivered, ts);
        var b = new DeliveryStatusUpdate("ext-123", MessageDeliveryStatus.Delivered, ts);

        a.Should().Be(b);
    }

    [Fact]
    public void ChannelConstraints_ShouldBeEqual_WhenAllPropertiesMatch()
    {
        var a = new ChannelConstraints(1600, TimeSpan.FromHours(24), true, true, true, 16);
        var b = new ChannelConstraints(1600, TimeSpan.FromHours(24), true, true, true, 16);

        a.Should().Be(b);
    }

    [Fact]
    public void PipelineResult_ShouldBeEqual_WhenAllPropertiesMatch()
    {
        var convId = EntityId.New();
        var contactId = EntityId.New();
        var msgId = EntityId.New();
        var a = new PipelineResult(convId, contactId, msgId, true);
        var b = new PipelineResult(convId, contactId, msgId, true);

        a.Should().Be(b);
    }

    [Fact]
    public void WebhookResult_ShouldHaveCorrectType_WhenIgnored()
    {
        var result = new WebhookResult(WebhookResultType.Ignored, null, null);

        result.Type.Should().Be(WebhookResultType.Ignored);
        result.Message.Should().BeNull();
        result.StatusUpdate.Should().BeNull();
    }
}
