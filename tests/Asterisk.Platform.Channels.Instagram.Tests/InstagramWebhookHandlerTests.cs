using System.Security.Cryptography;
using System.Text;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Instagram;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Asterisk.Platform.Channels.Instagram.Tests;

public class InstagramWebhookHandlerTests
{
    private const string AppSecret = "test-app-secret-99999";
    private const string VerifyToken = "ig-verify-token";
    private const string AccountId = "ig-account-123";

    private static readonly TenantId TenantA = new("tenant-a");

    private static InstagramWebhookHandler CreateHandler(
        string? appSecret = null,
        string? verifyToken = null)
    {
        var options = Options.Create(new InstagramOptions
        {
            InstagramAccountId = AccountId,
            PageAccessToken = "EAAtest",
            WebhookVerifyToken = verifyToken ?? VerifyToken,
            AppSecret = appSecret ?? AppSecret,
        });
        var configStore = Substitute.For<ITenantChannelConfigStore>();
        configStore.GetAsync(Arg.Any<TenantId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>())
            .Returns((TenantChannelConfig?)null);
        return new InstagramWebhookHandler(options, configStore, NullLogger<InstagramWebhookHandler>.Instance);
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

    // ── DM text message ───────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_WhenDmTextReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "object": "instagram",
              "entry": [{
                "id": "ig-account-123",
                "messaging": [{
                  "sender": { "id": "iguser-789" },
                  "recipient": { "id": "ig-account-123" },
                  "timestamp": 1700000000000,
                  "message": {
                    "mid": "ig_m_abc123",
                    "text": "Hey Instagram!"
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
        result.Message!.ExternalMessageId.Should().Be("ig_m_abc123");
        result.Message.From.Channel.Should().Be(ChannelType.Instagram);
        result.Message.From.Address.Should().Be("iguser-789");
        var textBlock = result.Message.Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        textBlock.Text.Should().Be("Hey Instagram!");
    }

    // ── DM image ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_WhenDmImageReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "object": "instagram",
              "entry": [{
                "id": "ig-account-123",
                "messaging": [{
                  "sender": { "id": "iguser-789" },
                  "recipient": { "id": "ig-account-123" },
                  "timestamp": 1700000100000,
                  "message": {
                    "mid": "ig_m_img001",
                    "attachments": [{
                      "type": "image",
                      "payload": { "url": "https://example.com/igphoto.jpg" }
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
        imageBlock.Url.Should().Be("https://example.com/igphoto.jpg");
    }

    // ── Comment/mention events are ignored ────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenChangesEventReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "object": "instagram",
              "entry": [{
                "id": "ig-account-123",
                "changes": [{
                  "field": "comments",
                  "value": {
                    "from": { "id": "iguser-999" },
                    "text": "Nice post!"
                  }
                }]
              }]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));
        var headers = SignedHeaders(body);

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    // ── HMAC validation ───────────────────────────────────────────────────────

    [Fact]
    public void ValidateSignature_ShouldReturnTrue_WhenHmacIsCorrect()
    {
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"instagram\"}"));
        var headers = SignedHeaders(body);

        var valid = InstagramWebhookHandler.ValidateSignature(body, headers, AppSecret);

        valid.Should().BeTrue();
    }

    [Fact]
    public void ValidateSignature_ShouldReturnFalse_WhenHmacIsWrong()
    {
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"instagram\"}"));
        var wrongHeaders = SignedHeaders(body, "bad-secret");

        var valid = InstagramWebhookHandler.ValidateSignature(body, wrongHeaders, AppSecret);

        valid.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenSignatureIsMissing()
    {
        var handler = CreateHandler();
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"object\":\"instagram\"}"));
        var headers = new Dictionary<string, string>();

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }
}
