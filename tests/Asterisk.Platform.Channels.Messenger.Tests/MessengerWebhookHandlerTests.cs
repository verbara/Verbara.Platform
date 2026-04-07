using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Messenger;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Platform.Channels.Messenger.Tests;

public class MessengerWebhookHandlerTests
{
    private const string AppSecret = "test-app-secret-12345";
    private const string VerifyToken = "my-verify-token";
    private const string PageId = "page-123";

    private static readonly TenantId TenantA = new("tenant-a");

    private static MessengerWebhookHandler CreateHandler(
        string? appSecret = null,
        string? verifyToken = null)
    {
        var options = Options.Create(new MessengerOptions
        {
            PageId = PageId,
            PageAccessToken = "EAAtest",
            WebhookVerifyToken = verifyToken ?? VerifyToken,
            AppSecret = appSecret ?? AppSecret,
        });
        var configStore = Substitute.For<ITenantChannelConfigStore>();
        configStore.GetAsync(Arg.Any<TenantId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>())
            .Returns((TenantChannelConfig?)null);
        return new MessengerWebhookHandler(options, configStore, NullLogger<MessengerWebhookHandler>.Instance);
    }

    private static Dictionary<string, string> SignedHeaders(
        ReadOnlyMemory<byte> body,
        string? secret = null)
    {
        var key = Encoding.UTF8.GetBytes(secret ?? AppSecret);
        var hash = HMACSHA256.HashData(key, body.Span);
        var sig = "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
        return new Dictionary<string, string> { ["x-hub-signature-256"] = sig };
    }

    // ── Text message ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_WhenTextMessageReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "object": "page",
              "entry": [{
                "id": "page-123",
                "messaging": [{
                  "sender": { "id": "user-456" },
                  "recipient": { "id": "page-123" },
                  "timestamp": 1700000000000,
                  "message": {
                    "mid": "m_abc123",
                    "text": "Hello Messenger!"
                  }
                }]
              }]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        result.Message.Should().NotBeNull();
        result.Message!.ExternalMessageId.Should().Be("m_abc123");
        result.Message.From.Channel.Should().Be(ChannelType.Messenger);
        result.Message.From.Address.Should().Be("user-456");
        var textBlock = result.Message.Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        textBlock.Text.Should().Be("Hello Messenger!");
    }

    // ── Image attachment ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_WhenImageAttachmentReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "object": "page",
              "entry": [{
                "id": "page-123",
                "messaging": [{
                  "sender": { "id": "user-456" },
                  "recipient": { "id": "page-123" },
                  "timestamp": 1700000100000,
                  "message": {
                    "mid": "m_img001",
                    "attachments": [{
                      "type": "image",
                      "payload": { "url": "https://example.com/photo.jpg" }
                    }]
                  }
                }]
              }]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        var imageBlock = result.Message!.Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<ImageBlock>().Subject;
        imageBlock.Url.Should().Be("https://example.com/photo.jpg");
    }

    // ── Delivery status ───────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnStatusUpdate_WhenDeliveryEventReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "object": "page",
              "entry": [{
                "id": "page-123",
                "messaging": [{
                  "sender": { "id": "user-456" },
                  "recipient": { "id": "page-123" },
                  "timestamp": 1700000200000,
                  "delivery": {
                    "mids": ["m_out001"],
                    "watermark": 1700000190000
                  }
                }]
              }]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.StatusUpdate);
        result.StatusUpdate.Should().NotBeNull();
        result.StatusUpdate!.ExternalMessageId.Should().Be("m_out001");
        result.StatusUpdate.NewStatus.Should().Be(MessageDeliveryStatus.Delivered);
    }

    // ── HMAC validation pass ──────────────────────────────────────────────────

    [Fact]
    public void ValidateSignature_ShouldReturnTrue_WhenHmacIsCorrect()
    {
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"page\"}"));
        var headers = SignedHeaders(body);

        var valid = MessengerWebhookHandler.ValidateSignature(body, headers, AppSecret);

        valid.Should().BeTrue();
    }

    // ── HMAC validation fail ──────────────────────────────────────────────────

    [Fact]
    public void ValidateSignature_ShouldReturnFalse_WhenHmacIsWrong()
    {
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"page\"}"));
        var wrongHeaders = SignedHeaders(body, "wrong-secret");

        var valid = MessengerWebhookHandler.ValidateSignature(body, wrongHeaders, AppSecret);

        valid.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenSignatureIsMissing()
    {
        var handler = CreateHandler();
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"page\"}"));
        var headers = new Dictionary<string, string>();

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    // ── Verification challenge ────────────────────────────────────────────────

    [Fact]
    public void HandleChallenge_ShouldReturnChallenge_WhenTokenMatches()
    {
        var handler = CreateHandler();
        var queryParams = new Dictionary<string, string>
        {
            ["hub.mode"] = "subscribe",
            ["hub.verify_token"] = VerifyToken,
            ["hub.challenge"] = "challenge-xyz",
        };

        var result = handler.HandleChallenge(queryParams);

        result.Should().Be("challenge-xyz");
    }

    [Fact]
    public void HandleChallenge_ShouldReturnNull_WhenTokenDoesNotMatch()
    {
        var handler = CreateHandler();
        var queryParams = new Dictionary<string, string>
        {
            ["hub.mode"] = "subscribe",
            ["hub.verify_token"] = "wrong-token",
            ["hub.challenge"] = "challenge-xyz",
        };

        var result = handler.HandleChallenge(queryParams);

        result.Should().BeNull();
    }

    // ── Invalid JSON ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenPayloadIsInvalidJson()
    {
        var handler = CreateHandler();
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("NOT_JSON"));
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }
}
