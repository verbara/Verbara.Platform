using System.Text;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Telegram;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Telegram.Tests;

public class TelegramWebhookHandlerTests
{
    private const string WebhookSecret = "my-webhook-secret";
    private static readonly TenantId TenantA = new("tenant-a");

    private static TelegramWebhookHandler CreateHandler(string? webhookSecret = null) =>
        new(
            Options.Create(new TelegramOptions
            {
                BotToken = "123456:ABCdef",
                WebhookSecret = webhookSecret,
            }),
            NullLogger<TelegramWebhookHandler>.Instance);

    private static Dictionary<string, string> SecretHeaders(string secret) =>
        new() { ["x-telegram-bot-api-secret-token"] = secret };

    // ── Text message ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_WhenTextMessageReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "update_id": 100,
              "message": {
                "message_id": 42,
                "chat": { "id": 987654321, "first_name": "Alice", "username": "alice" },
                "from": { "id": 987654321, "first_name": "Alice" },
                "text": "Hello from Telegram!",
                "date": 1700000000
              }
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        result.Message.Should().NotBeNull();
        result.Message!.ExternalMessageId.Should().Be("42");
        result.Message.From.Channel.Should().Be(ChannelType.Telegram);
        result.Message.From.Address.Should().Be("987654321");
        var textBlock = result.Message.Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<TextBlock>().Subject;
        textBlock.Text.Should().Be("Hello from Telegram!");
    }

    // ── Photo message ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_WhenPhotoMessageReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "update_id": 101,
              "message": {
                "message_id": 43,
                "chat": { "id": 111, "first_name": "Bob" },
                "photo": [
                  { "file_id": "small_id", "width": 90, "height": 90 },
                  { "file_id": "large_id", "width": 800, "height": 600 }
                ],
                "date": 1700000001
              }
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        var imageBlock = result.Message!.Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<ImageBlock>().Subject;
        // Should pick the largest (last) photo
        imageBlock.Url.Should().Be("telegram-file:large_id");
        imageBlock.MimeType.Should().Be("image/jpeg");
    }

    // ── Document message ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_WhenDocumentMessageReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "update_id": 102,
              "message": {
                "message_id": 44,
                "chat": { "id": 222, "first_name": "Carol" },
                "document": {
                  "file_id": "doc_file_id",
                  "file_name": "report.pdf",
                  "mime_type": "application/pdf",
                  "file_size": 102400
                },
                "date": 1700000002
              }
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        var fileBlock = result.Message!.Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<FileBlock>().Subject;
        fileBlock.FileName.Should().Be("report.pdf");
        fileBlock.MimeType.Should().Be("application/pdf");
        fileBlock.Url.Should().Be("telegram-file:doc_file_id");
    }

    // ── Location message ──────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnNewMessage_WhenLocationMessageReceived()
    {
        var handler = CreateHandler();
        var json = """
            {
              "update_id": 103,
              "message": {
                "message_id": 45,
                "chat": { "id": 333, "first_name": "Dave" },
                "location": { "latitude": 48.8566, "longitude": 2.3522 },
                "date": 1700000003
              }
            }
            """;
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.NewMessage);
        var locBlock = result.Message!.Content.Blocks.Should().ContainSingle().Which.Should().BeOfType<LocationBlock>().Subject;
        locBlock.Latitude.Should().BeApproximately(48.8566, 0.0001);
        locBlock.Longitude.Should().BeApproximately(2.3522, 0.0001);
    }

    // ── Webhook secret validation — success ───────────────────────────────────

    [Fact]
    public void ValidateSecret_ShouldReturnTrue_WhenSecretMatches()
    {
        var handler = CreateHandler(WebhookSecret);
        var headers = SecretHeaders(WebhookSecret);

        var valid = handler.ValidateSecret(headers);

        valid.Should().BeTrue();
    }

    // ── Webhook secret validation — wrong secret ──────────────────────────────

    [Fact]
    public void ValidateSecret_ShouldReturnFalse_WhenSecretDoesNotMatch()
    {
        var handler = CreateHandler(WebhookSecret);
        var headers = SecretHeaders("wrong-secret");

        var valid = handler.ValidateSecret(headers);

        valid.Should().BeFalse();
    }

    // ── Webhook secret validation — header missing ────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenSecretRequiredButHeaderMissing()
    {
        var handler = CreateHandler(WebhookSecret);
        var json = """{ "update_id": 999, "message": null }""";
        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(json));

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    // ── Non-message updates are ignored ──────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenUpdateHasNoMessage()
    {
        var handler = CreateHandler();
        // edited_message / channel_post — only "message" field triggers inbound
        var json = """
            {
              "update_id": 200,
              "edited_message": {
                "message_id": 50,
                "chat": { "id": 444, "first_name": "Eve" },
                "text": "edited text",
                "date": 1700000100
              }
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

    // ── No secret configured — no validation ─────────────────────────────────

    [Fact]
    public void ValidateSecret_ShouldReturnTrue_WhenNoSecretConfigured()
    {
        var handler = CreateHandler(webhookSecret: null);

        // Any headers (or none) should pass when no secret is configured
        var valid = handler.ValidateSecret(new Dictionary<string, string>());

        valid.Should().BeTrue();
    }
}
