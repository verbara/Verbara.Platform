using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.WhatsApp;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.WhatsApp.Tests;

public class WhatsAppWebhookHandlerTests
{
    private const string AppSecret = "test-app-secret-12345";
    private const string VerifyToken = "my-verify-token";

    private static readonly TenantId TenantA = new("tenant-a");

    private static WhatsAppWebhookHandler CreateHandler(
        string? appSecret = null,
        string? verifyToken = null)
    {
        var options = Options.Create(new WhatsAppOptions
        {
            PhoneNumberId = "123456789",
            AccessToken = "EAAtest",
            WebhookVerifyToken = verifyToken ?? VerifyToken,
            AppSecret = appSecret ?? AppSecret,
        });
        return new WhatsAppWebhookHandler(options, NullLogger<WhatsAppWebhookHandler>.Instance);
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
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "WABAID",
                "changes": [{
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": { "display_phone_number": "15550000001", "phone_number_id": "123456789" },
                    "contacts": [{ "profile": { "name": "Alice" }, "wa_id": "15550000002" }],
                    "messages": [{
                      "from": "15550000002",
                      "id": "wamid.abc123",
                      "timestamp": "1700000000",
                      "type": "text",
                      "text": { "body": "Hello there!" }
                    }]
                  },
                  "field": "messages"
                }]
              }]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        result.Message.Should().NotBeNull();
        result.Message!.ExternalMessageId.Should().Be("wamid.abc123");
        result.Message.From.Channel.Should().Be(ChannelType.WhatsApp);
        result.Message.From.Address.Should().Be("15550000002");
        var textBlock = result.Message.Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        textBlock.Text.Should().Be("Hello there!");
    }

    // ── Image message ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_WhenImageMessageReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "WABAID",
                "changes": [{
                  "value": {
                    "messaging_product": "whatsapp",
                    "messages": [{
                      "from": "15550000002",
                      "id": "wamid.img001",
                      "timestamp": "1700000100",
                      "type": "image",
                      "image": {
                        "id": "media-id-999",
                        "caption": "Look at this!",
                        "mime_type": "image/jpeg",
                        "sha256": "abc"
                      }
                    }]
                  },
                  "field": "messages"
                }]
              }]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        var imageBlock = result.Message!.Content.Blocks
            .Should().ContainSingle().Which
            .Should().BeOfType<ImageBlock>().Subject;
        imageBlock.Caption.Should().Be("Look at this!");
        imageBlock.MimeType.Should().Be("image/jpeg");
    }

    // ── Status update: delivered ──────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnStatusUpdate_WhenDeliveredStatusReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "WABAID",
                "changes": [{
                  "value": {
                    "messaging_product": "whatsapp",
                    "statuses": [{
                      "id": "wamid.out001",
                      "status": "delivered",
                      "timestamp": "1700000200",
                      "recipient_id": "15550000002"
                    }]
                  },
                  "field": "messages"
                }]
              }]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.StatusUpdate);
        result.StatusUpdate.Should().NotBeNull();
        result.StatusUpdate!.ExternalMessageId.Should().Be("wamid.out001");
        result.StatusUpdate.NewStatus.Should().Be(MessageDeliveryStatus.Delivered);
    }

    // ── Status update: read ───────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnStatusUpdate_WhenReadStatusReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "WABAID",
                "changes": [{
                  "value": {
                    "messaging_product": "whatsapp",
                    "statuses": [{
                      "id": "wamid.out002",
                      "status": "read",
                      "timestamp": "1700000300",
                      "recipient_id": "15550000002"
                    }]
                  },
                  "field": "messages"
                }]
              }]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.StatusUpdate);
        result.StatusUpdate!.NewStatus.Should().Be(MessageDeliveryStatus.Read);
    }

    // ── HMAC validation success ───────────────────────────────────────────────

    [Fact]
    public void ValidateSignature_ShouldReturnTrue_WhenHmacIsCorrect()
    {
        var handler = CreateHandler();
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"test\"}"));
        var headers = SignedHeaders(body);

        var valid = handler.ValidateSignature(body, headers);

        valid.Should().BeTrue();
    }

    // ── HMAC validation failure ───────────────────────────────────────────────

    [Fact]
    public void ValidateSignature_ShouldReturnFalse_WhenHmacIsWrong()
    {
        var handler = CreateHandler();
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"test\"}"));
        // Sign with wrong secret
        var wrongHeaders = SignedHeaders(body, "wrong-secret");

        var valid = handler.ValidateSignature(body, wrongHeaders);

        valid.Should().BeFalse();
    }

    [Fact]
    public void ValidateSignature_ShouldReturnFalse_WhenSignatureHeaderMissing()
    {
        var handler = CreateHandler();
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"test\"}"));
        var headers = new Dictionary<string, string>();

        var valid = handler.ValidateSignature(body, headers);

        valid.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenSignatureIsInvalid()
    {
        var handler = CreateHandler();
        var json = """{"object":"test"}""";
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));
        // Missing signature header
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

    [Fact]
    public void HandleChallenge_ShouldReturnNull_WhenModeIsNotSubscribe()
    {
        var handler = CreateHandler();
        var queryParams = new Dictionary<string, string>
        {
            ["hub.mode"] = "unsubscribe",
            ["hub.verify_token"] = VerifyToken,
            ["hub.challenge"] = "challenge-xyz",
        };

        var result = handler.HandleChallenge(queryParams);

        result.Should().BeNull();
    }

    // ── Ignored events ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenPayloadHasNoMessages()
    {
        var handler = CreateHandler();
        var json = """
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "WABAID",
                "changes": [{
                  "value": {
                    "messaging_product": "whatsapp"
                  },
                  "field": "messages"
                }]
              }]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenFieldIsNotMessages()
    {
        var handler = CreateHandler();
        var json = """
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "WABAID",
                "changes": [{
                  "value": { "messaging_product": "whatsapp" },
                  "field": "account_update"
                }]
              }]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenPayloadIsInvalidJson()
    {
        var handler = CreateHandler();
        var badJson = "NOT_JSON";
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(badJson));
        // Use correct signature so we get past validation
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }
}
