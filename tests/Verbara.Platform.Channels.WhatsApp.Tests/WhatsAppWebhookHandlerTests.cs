using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WhatsApp;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Verbara.Platform.Channels.WhatsApp.Tests;

public class WhatsAppWebhookHandlerTests
{
    private const string AppSecret = "test-app-secret-12345";
    private const string VerifyToken = "my-verify-token";
    private const string PhoneNumberId = "123456789";

    private static readonly TenantId TenantA = new("tenant-a");

    private static WhatsAppWebhookHandler CreateHandler(
        string? appSecret = null,
        string? verifyToken = null,
        bool isActive = true)
    {
        var configStore = Substitute.For<ITenantChannelConfigStore>();
        configStore.GetAsync(TenantA, ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(new TenantChannelConfig
            {
                TenantId = TenantA,
                Channel = ChannelType.WhatsApp,
                IsActive = isActive,
                Credentials = new Dictionary<string, string>
                {
                    [WhatsAppCredentialKeys.AccessToken] = "EAAtest",
                    [WhatsAppCredentialKeys.PhoneNumberId] = PhoneNumberId,
                    [WhatsAppCredentialKeys.AppSecret] = appSecret ?? AppSecret,
                    [WhatsAppCredentialKeys.WebhookVerifyToken] = verifyToken ?? VerifyToken,
                },
            });
        return new WhatsAppWebhookHandler(configStore, NullLogger<WhatsAppWebhookHandler>.Instance);
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
        result.Messages.Should().ContainSingle();
        result.Messages[0].ExternalMessageId.Should().Be("wamid.abc123");
        result.Messages[0].From.Channel.Should().Be(ChannelType.WhatsApp);
        result.Messages[0].From.Address.Should().Be("15550000002");
        var textBlock = result.Messages[0].Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
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
                    "metadata": { "display_phone_number": "15550000001", "phone_number_id": "123456789" },
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
        var imageBlock = result.Messages[0].Content.Blocks
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
                    "metadata": { "display_phone_number": "15550000001", "phone_number_id": "123456789" },
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
        result.StatusUpdates.Should().ContainSingle();
        result.StatusUpdates[0].ExternalMessageId.Should().Be("wamid.out001");
        result.StatusUpdates[0].NewStatus.Should().Be(MessageDeliveryStatus.Delivered);
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
                    "metadata": { "display_phone_number": "15550000001", "phone_number_id": "123456789" },
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
        result.StatusUpdates[0].NewStatus.Should().Be(MessageDeliveryStatus.Read);
    }

    // ── HMAC validation success ───────────────────────────────────────────────

    [Fact]
    public void ValidateSignature_ShouldReturnTrue_WhenHmacIsCorrect()
    {
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"test\"}"));
        var headers = SignedHeaders(body);

        var valid = WhatsAppWebhookHandler.ValidateSignature(body, headers, AppSecret);

        valid.Should().BeTrue();
    }

    // ── HMAC validation failure ───────────────────────────────────────────────

    [Fact]
    public void ValidateSignature_ShouldReturnFalse_WhenHmacIsWrong()
    {
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"test\"}"));
        // Sign with wrong secret
        var wrongHeaders = SignedHeaders(body, "wrong-secret");

        var valid = WhatsAppWebhookHandler.ValidateSignature(body, wrongHeaders, AppSecret);

        valid.Should().BeFalse();
    }

    [Fact]
    public void ValidateSignature_ShouldReturnFalse_WhenSignatureHeaderMissing()
    {
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"test\"}"));
        var headers = new Dictionary<string, string>();

        var valid = WhatsAppWebhookHandler.ValidateSignature(body, headers, AppSecret);

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

    [Fact]
    public void ValidateSignature_ShouldReturnTrue_WhenHexIsUppercase()
    {
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"test\"}"));
        var headers = SignedHeaders(body);
        headers["x-hub-signature-256"] = "sha256=" + headers["x-hub-signature-256"]["sha256=".Length..].ToUpperInvariant();

        WhatsAppWebhookHandler.ValidateSignature(body, headers, AppSecret).Should().BeTrue();
    }

    [Theory]
    [InlineData("sha256=")]
    [InlineData("sha256=zz")]
    [InlineData("sha1=0000000000000000000000000000000000000000")]
    [InlineData("sha256=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ValidateSignature_ShouldReturnFalse_WhenHeaderIsMalformed(string header)
    {
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{}"));
        var headers = new Dictionary<string, string> { ["x-hub-signature-256"] = header };

        WhatsAppWebhookHandler.ValidateSignature(body, headers, AppSecret).Should().BeFalse();
    }

    [Fact]
    public void ValidateSignature_ShouldUseFixedTimeEquals_WhenComparingHmac()
    {
        // Structural: the digest comparison must be constant time. A string/sequence comparison exits at the
        // first differing character and leaks how much of a forged signature is right.
        var source = File.ReadAllText(Path.Join(
            RepoRoot(), "src", "Verbara.Platform.Channels.WhatsApp", "WhatsAppWebhookHandler.cs"));
        var method = source[source.IndexOf("internal static bool ValidateSignature", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("\n    }", StringComparison.Ordinal)];

        method.Should().Contain("CryptographicOperations.FixedTimeEquals(");
        method.Should().NotContain("string.Equals(").And.NotContain("SequenceEqual(").And.NotContain(" == ");
    }

    // ── Verification handshake (per tenant) ───────────────────────────────────

    [Fact]
    public async Task VerifySubscriptionAsync_ShouldReturnChallenge_WhenTokenMatches()
    {
        var handler = CreateHandler();

        var result = await handler.VerifySubscriptionAsync(TenantA, "subscribe", VerifyToken, "challenge-xyz", CancellationToken.None);

        result.Should().Be("challenge-xyz");
    }

    [Fact]
    public async Task VerifySubscriptionAsync_ShouldReturnNull_WhenTokenDoesNotMatch()
    {
        var handler = CreateHandler();

        var result = await handler.VerifySubscriptionAsync(TenantA, "subscribe", "wrong-token", "challenge-xyz", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task VerifySubscriptionAsync_ShouldReturnNull_WhenModeIsNotSubscribe()
    {
        var handler = CreateHandler();

        var result = await handler.VerifySubscriptionAsync(TenantA, "unsubscribe", VerifyToken, "challenge-xyz", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task VerifySubscriptionAsync_ShouldReturnNull_WhenChannelIsInactive()
    {
        var handler = CreateHandler(isActive: false);

        var result = await handler.VerifySubscriptionAsync(TenantA, "subscribe", VerifyToken, "challenge-xyz", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task VerifySubscriptionAsync_ShouldReturnNull_WhenTenantHasNoConfig()
    {
        var handler = CreateHandler();

        var result = await handler.VerifySubscriptionAsync(new TenantId("tenant-other"), "subscribe", VerifyToken, "challenge-xyz", CancellationToken.None);

        result.Should().BeNull();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "Verbara.Platform.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Verbara.Platform.slnx) not found.");
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
