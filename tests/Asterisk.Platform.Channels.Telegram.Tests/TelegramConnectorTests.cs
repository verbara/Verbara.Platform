using System.Net;
using System.Text;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Telegram;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Telegram.Tests;

public class TelegramConnectorTests
{
    private const string BotToken = "123456:ABCdefGHI";
    private const string BaseUrl = "https://api.telegram.org";

    private static TelegramOptions DefaultOptions() => new()
    {
        BotToken = BotToken,
        BaseUrl = BaseUrl,
    };

    private static (TelegramConnector connector, FakeTelegramHttpHandler handler) CreateConnector(
        HttpResponseMessage? response = null,
        TelegramOptions? options = null)
    {
        var fakeHandler = new FakeTelegramHttpHandler(
            response ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{ "ok": true, "result": { "message_id": 999 } }""",
                    Encoding.UTF8,
                    "application/json"),
            });

        var httpClient = new HttpClient(fakeHandler);
        var connector = new TelegramConnector(
            httpClient,
            Options.Create(options ?? DefaultOptions()),
            NullLogger<TelegramConnector>.Instance);

        return (connector, fakeHandler);
    }

    private static OutboundMessage MakeMessage(string chatId, MessageBlock block) =>
        new(
            new ChannelAddress(ChannelType.Telegram, chatId),
            new MessageEnvelope([block]),
            new TenantId("tenant-a"),
            EntityId.From("conv-1"));

    // ── Send text ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldSendTextMessage_WhenTextBlockProvided()
    {
        var (connector, handler) = CreateConnector();

        var result = await connector.SendAsync(
            MakeMessage("987654321", new TextBlock("Hello Telegram!")),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalMessageId.Should().Be("999");

        handler.LastRequestUri.Should().Contain("/sendMessage");
        handler.LastRequestBodyString.Should().Contain("\"text\":\"Hello Telegram!\"");
        handler.LastRequestBodyString.Should().Contain("\"chat_id\":987654321");
    }

    // ── Send photo ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldSendPhoto_WhenImageBlockProvided()
    {
        var (connector, handler) = CreateConnector();

        var result = await connector.SendAsync(
            MakeMessage("111", new ImageBlock("https://example.com/photo.jpg", "Nice photo", "image/jpeg")),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        handler.LastRequestUri.Should().Contain("/sendPhoto");
        handler.LastRequestBodyString.Should().Contain("\"photo\":\"https://example.com/photo.jpg\"");
        handler.LastRequestBodyString.Should().Contain("\"caption\":\"Nice photo\"");
    }

    // ── Correct URL format with bot token ─────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldPostToCorrectUrl_WithBotToken()
    {
        var (connector, handler) = CreateConnector();

        await connector.SendAsync(
            MakeMessage("111", new TextBlock("test")),
            CancellationToken.None);

        handler.LastRequestUri.Should().Be($"{BaseUrl}/bot{BotToken}/sendMessage");
    }

    // ── API error response ────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenApiReturnsError()
    {
        var errorResponse = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{ "ok": false, "error_code": 400, "description": "Bad Request: chat not found" }""",
                Encoding.UTF8,
                "application/json"),
        };

        var (connector, _) = CreateConnector(errorResponse);

        var result = await connector.SendAsync(
            MakeMessage("999", new TextBlock("Hi")),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("400");
        result.ErrorMessage.Should().Contain("chat not found");
    }

    // ── GetStatusAsync always returns null ────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_ShouldReturnNull_Always()
    {
        var (connector, _) = CreateConnector();

        var status = await connector.GetStatusAsync("999", CancellationToken.None);

        status.Should().BeNull();
    }

    // ── Channel property ──────────────────────────────────────────────────────

    [Fact]
    public void Channel_ShouldBeTelegram()
    {
        var (connector, _) = CreateConnector();

        connector.Channel.Should().Be(ChannelType.Telegram);
    }
}

/// <summary>Captures outgoing HTTP requests for assertion in tests.</summary>
internal sealed class FakeTelegramHttpHandler(HttpResponseMessage response) : HttpMessageHandler
{
    public string? LastRequestBodyString { get; private set; }
    public string? LastRequestUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        if (request.Content is not null)
            LastRequestBodyString = await request.Content.ReadAsStringAsync(cancellationToken);
        return response;
    }
}
