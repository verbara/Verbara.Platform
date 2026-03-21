using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Rcs;
using Asterisk.Platform.Core;
using NSubstitute;

namespace Asterisk.Platform.Channels.Rcs.Tests;

public class RcsWebhookHandlerTests
{
    private static readonly TenantId TestTenant = new("test-tenant");

    private static RcsWebhookHandler CreateHandler(IRcsProvider? provider = null)
    {
        provider ??= Substitute.For<IRcsProvider>();
        return new RcsWebhookHandler(provider);
    }

    private static ReadOnlyMemory<byte> TextMessageBody(string messageId = "msg-abc", string text = "Hello agent") =>
        Encoding.UTF8.GetBytes(
            $"{{\"messageId\":\"{messageId}\",\"text\":\"{text}\",\"senderPhoneNumber\":\"+15551234567\"}}");

    private static ReadOnlyMemory<byte> DeliveryReceiptBody(string messageId = "msg-abc", string status = "DELIVERED") =>
        Encoding.UTF8.GetBytes(
            $"{{\"messageId\":\"{messageId}\",\"deliveryReceipt\":{{\"status\":\"{status}\"}}}}");

    [Fact]
    public void Channel_ShouldBeRcs()
    {
        var handler = CreateHandler();

        handler.Channel.Should().Be(ChannelType.Rcs);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnAccepted_WhenTextMessageReceived()
    {
        var handler = CreateHandler();
        var body = TextMessageBody();
        var headers = new Dictionary<string, string>();

        var result = await handler.HandleAsync(body, headers, TestTenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnStatusUpdate_WhenDeliveredReceiptReceived()
    {
        var provider = Substitute.For<IRcsProvider>();
        provider.GetStatusAsync("msg-delivered", Arg.Any<CancellationToken>())
                .Returns(RcsDeliveryStatus.Delivered);
        var handler = CreateHandler(provider);
        var body = DeliveryReceiptBody("msg-delivered", "DELIVERED");
        var headers = new Dictionary<string, string>();

        var result = await handler.HandleAsync(body, headers, TestTenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.StatusUpdate);
        result.StatusUpdate.Should().NotBeNull();
        result.StatusUpdate!.ExternalMessageId.Should().Be("msg-delivered");
        result.StatusUpdate.NewStatus.Should().Be(Conversations.MessageDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnStatusUpdate_WhenReadReceiptReceived()
    {
        var provider = Substitute.For<IRcsProvider>();
        provider.GetStatusAsync("msg-read", Arg.Any<CancellationToken>())
                .Returns(RcsDeliveryStatus.Read);
        var handler = CreateHandler(provider);
        var body = DeliveryReceiptBody("msg-read", "READ");
        var headers = new Dictionary<string, string>();

        var result = await handler.HandleAsync(body, headers, TestTenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.StatusUpdate);
        result.StatusUpdate!.NewStatus.Should().Be(Conversations.MessageDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenPayloadIsEmptyObject()
    {
        var handler = CreateHandler();
        var body = Encoding.UTF8.GetBytes("{}");
        var headers = new Dictionary<string, string>();

        var result = await handler.HandleAsync(body, headers, TestTenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenPayloadIsInvalidJson()
    {
        var handler = CreateHandler();
        var body = Encoding.UTF8.GetBytes("not json");
        var headers = new Dictionary<string, string>();

        var result = await handler.HandleAsync(body, headers, TestTenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnRejected_WhenSignatureIsInvalid()
    {
        var handler = CreateHandler();
        var body = TextMessageBody();
        var headers = new Dictionary<string, string>
        {
            ["X-Goog-Signature"] = "invalidsignature==",
            ["X-Goog-Signature-Secret"] = "mysecret",
        };

        var result = await handler.HandleAsync(body, headers, TestTenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldAcceptRequest_WhenValidSignatureProvided()
    {
        var handler = CreateHandler();
        var bodyBytes = TextMessageBody();
        var secret = "webhook-secret";
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var hash = HMACSHA256.HashData(keyBytes, bodyBytes.Span);
        var signature = Convert.ToBase64String(hash);

        var headers = new Dictionary<string, string>
        {
            ["X-Goog-Signature"] = signature,
            ["X-Goog-Signature-Secret"] = secret,
        };

        var result = await handler.HandleAsync(bodyBytes, headers, TestTenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenNoSignatureAndNoRelevantFields()
    {
        var handler = CreateHandler();
        var body = Encoding.UTF8.GetBytes("{\"senderPhoneNumber\":\"+15551111111\"}");
        var headers = new Dictionary<string, string>();

        var result = await handler.HandleAsync(body, headers, TestTenant, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }
}
