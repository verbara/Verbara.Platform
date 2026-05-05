using System.Security.Cryptography;
using System.Text;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Twitter;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Channels.Twitter.Tests;

public class TwitterWebhookHandlerTests
{
    private const string ApiSecret = "my-api-secret";
    private static readonly TenantId TenantA = new("tenant-a");

    private static TwitterWebhookHandler CreateHandler(string? apiSecret = null)
    {
        var configStore = Substitute.For<ITenantChannelConfigStore>();
        configStore.GetAsync(Arg.Any<TenantId>(), Arg.Any<ChannelType>(), Arg.Any<CancellationToken>())
            .Returns((TenantChannelConfig?)null);
        return new TwitterWebhookHandler(
            Options.Create(new TwitterOptions
            {
                ApiKey = "api-key",
                ApiSecret = apiSecret ?? ApiSecret,
                AccessToken = "access-token",
                AccessTokenSecret = "access-token-secret",
                BearerToken = "bearer-token",
            }),
            configStore,
            NullLogger<TwitterWebhookHandler>.Instance);
    }

    private static string ComputeSignature(string secret, string body)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hash = HMACSHA256.HashData(keyBytes, bodyBytes);
        return $"sha256={Convert.ToBase64String(hash)}";
    }

    private static Dictionary<string, string> SignedHeaders(string body, string? secret = null) =>
        new() { ["x-twitter-webhooks-signature"] = ComputeSignature(secret ?? ApiSecret, body) };

    // ── DM event: text message ────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_WhenDmEventReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "for_user_id": "123",
              "direct_message_events": [
                {
                  "type": "message_create",
                  "id": "dm-event-001",
                  "created_timestamp": "1700000000000",
                  "message_create": {
                    "target": { "recipient_id": "123" },
                    "sender_id": "456",
                    "message_data": { "text": "Hello from Twitter!" }
                  }
                }
              ]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        result.Message.Should().NotBeNull();
        result.Message!.ExternalMessageId.Should().Be("dm-event-001");
        result.Message.From.Channel.Should().Be(ChannelType.Twitter);
        result.Message.From.Address.Should().Be("456");
        var textBlock = result.Message.Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        textBlock.Text.Should().Be("Hello from Twitter!");
    }

    // ── CRC challenge response ────────────────────────────────────────────────

    [Fact]
    public void ComputeCrcResponse_ShouldReturnSha256Prefix_WithValidHmac()
    {
        const string crcToken = "abc123challengetoken";

        var response = TwitterWebhookHandler.ComputeCrcResponse(crcToken, ApiSecret);

        response.Should().StartWith("sha256=");
        // Verify the HMAC is correct
        var expected = ComputeSignature(ApiSecret, crcToken);
        response.Should().Be(expected);
    }

    // ── HMAC signature validation: valid ──────────────────────────────────────

    [Fact]
    public void ValidateHmacSignature_ShouldReturnTrue_WhenSignatureIsValid()
    {
        const string body = """{"for_user_id":"123"}""";
        var sig = ComputeSignature(ApiSecret, body);

        var valid = TwitterWebhookHandler.ValidateHmacSignature(Encoding.UTF8.GetBytes(body), sig, ApiSecret);

        valid.Should().BeTrue();
    }

    // ── HMAC signature validation: wrong secret ───────────────────────────────

    [Fact]
    public void ValidateHmacSignature_ShouldReturnFalse_WhenSignatureUsesWrongSecret()
    {
        const string body = """{"for_user_id":"123"}""";
        var sig = ComputeSignature("wrong-secret", body);

        var valid = TwitterWebhookHandler.ValidateHmacSignature(Encoding.UTF8.GetBytes(body), sig, ApiSecret);

        valid.Should().BeFalse();
    }

    // ── HMAC validation rejects bad prefix ────────────────────────────────────

    [Fact]
    public void ValidateHmacSignature_ShouldReturnFalse_WhenSignatureHasNoPrefixSha256()
    {
        const string body = "test";

        var valid = TwitterWebhookHandler.ValidateHmacSignature(Encoding.UTF8.GetBytes(body), "invalid-no-prefix", ApiSecret);

        valid.Should().BeFalse();
    }

    // ── Signature header present and valid → allow ────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_WhenSignatureHeaderIsValid()
    {
        var handler = CreateHandler();
        var json = """
            {
              "for_user_id": "123",
              "direct_message_events": [
                {
                  "type": "message_create",
                  "id": "dm-002",
                  "created_timestamp": "1700000001000",
                  "message_create": {
                    "target": { "recipient_id": "123" },
                    "sender_id": "789",
                    "message_data": { "text": "Verified DM" }
                  }
                }
              ]
            }
            """;
        var headers = SignedHeaders(json);
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
    }

    // ── Signature header present but wrong → reject ───────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenSignatureHeaderIsInvalid()
    {
        var handler = CreateHandler();
        var json = """{"for_user_id":"123","direct_message_events":[]}""";
        var headers = new Dictionary<string, string>
        {
            ["x-twitter-webhooks-signature"] = "sha256=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        };
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(body, headers, TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    // ── Ignore non-DM events (no direct_message_events) ──────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenNoDirectMessageEvents()
    {
        var handler = CreateHandler();
        // Simulates a follow event or tweet event — no direct_message_events field
        var json = """
            {
              "for_user_id": "123",
              "follow_events": [
                { "type": "follow", "created_timestamp": "1700000002000" }
              ]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    // ── Ignore non-message_create DM event types ─────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenDmEventTypeIsNotMessageCreate()
    {
        var handler = CreateHandler();
        var json = """
            {
              "for_user_id": "123",
              "direct_message_events": [
                {
                  "type": "message_delete",
                  "id": "dm-del-001",
                  "created_timestamp": "1700000003000"
                }
              ]
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    // ── Invalid JSON ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenPayloadIsInvalidJson()
    {
        var handler = CreateHandler();
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("NOT_JSON"));

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }
}
