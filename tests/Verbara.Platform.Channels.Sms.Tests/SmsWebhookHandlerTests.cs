using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Sms;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using NSubstitute;

namespace Verbara.Platform.Channels.Sms.Tests;

public class SmsWebhookHandlerTests
{
    private const string TestMessageId = "msg-abc";
    private const string TestTenant = "test-tenant";

    private static SmsWebhookHandler CreateHandler(ISmsProvider? provider = null)
    {
        provider ??= Substitute.For<ISmsProvider>();
        return new SmsWebhookHandler(provider);
    }

    [Fact]
    public void Channel_ShouldBeSms()
    {
        var handler = CreateHandler();

        handler.Channel.Should().Be(ChannelType.Sms);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnStatusUpdate_WhenDeliveredStatusHeaderPresent()
    {
        var provider = Substitute.For<ISmsProvider>();
        provider.GetStatusAsync(TestMessageId, Arg.Any<CancellationToken>())
                .Returns(SmsDeliveryStatus.Delivered);
        var handler = CreateHandler(provider);
        var headers = new Dictionary<string, string>
        {
            ["X-Message-Id"] = TestMessageId,
            ["X-Delivery-Status"] = "delivered",
        };

        var result = await handler.HandleAsync(
            ReadOnlyMemory<byte>.Empty, headers, new TenantId(TestTenant), CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.StatusUpdate);
        result.StatusUpdate.Should().NotBeNull();
        result.StatusUpdate!.ExternalMessageId.Should().Be(TestMessageId);
        result.StatusUpdate.NewStatus.Should().Be(MessageDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenMessageIdHeaderMissing()
    {
        var handler = CreateHandler();
        var headers = new Dictionary<string, string>
        {
            ["X-Delivery-Status"] = "delivered",
        };

        var result = await handler.HandleAsync(
            ReadOnlyMemory<byte>.Empty, headers, new TenantId(TestTenant), CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenStatusHeaderMissing()
    {
        var handler = CreateHandler();
        var headers = new Dictionary<string, string>
        {
            ["X-Message-Id"] = TestMessageId,
        };

        var result = await handler.HandleAsync(
            ReadOnlyMemory<byte>.Empty, headers, new TenantId(TestTenant), CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenStatusValueIsUnrecognized()
    {
        var handler = CreateHandler();
        var headers = new Dictionary<string, string>
        {
            ["X-Message-Id"] = "msg-xyz",
            ["X-Delivery-Status"] = "unknown_status",
        };

        var result = await handler.HandleAsync(
            ReadOnlyMemory<byte>.Empty, headers, new TenantId(TestTenant), CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldAcceptTwilioStyleHeaders_WhenMessageSidAndMessageStatusPresent()
    {
        var provider = Substitute.For<ISmsProvider>();
        provider.GetStatusAsync("SM123", Arg.Any<CancellationToken>())
                .Returns(SmsDeliveryStatus.Sent);
        var handler = CreateHandler(provider);
        var headers = new Dictionary<string, string>
        {
            ["MessageSid"] = "SM123",
            ["MessageStatus"] = "sent",
        };

        var result = await handler.HandleAsync(
            ReadOnlyMemory<byte>.Empty, headers, new TenantId(TestTenant), CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.StatusUpdate);
        result.StatusUpdate!.NewStatus.Should().Be(MessageDeliveryStatus.Sent);
    }
}
