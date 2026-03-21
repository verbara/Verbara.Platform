using System.Net;
using System.Text;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Messenger;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Asterisk.Platform.Channels.Messenger.Tests;

public class MessengerConnectorTests
{
    private const string PageId = "page-123";
    private const string PageAccessToken = "EAAtest";

    private static MessengerOptions DefaultOptions() => new()
    {
        PageId = PageId,
        PageAccessToken = PageAccessToken,
        WebhookVerifyToken = "token",
        AppSecret = "secret",
        ApiVersion = "v21.0",
        BaseUrl = "https://graph.facebook.com",
    };

    private static (MessengerConnector connector, FakeHttpMessageHandler handler) CreateConnector(
        HttpResponseMessage? response = null,
        MessengerOptions? options = null)
    {
        var fakeHandler = new FakeHttpMessageHandler(
            response ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    { "recipient_id": "user-456", "message_id": "m_sent001" }
                    """, Encoding.UTF8, "application/json"),
            });

        var httpClient = new HttpClient(fakeHandler);
        var connector = new MessengerConnector(
            httpClient,
            Options.Create(options ?? DefaultOptions()),
            NullLogger<MessengerConnector>.Instance);

        return (connector, fakeHandler);
    }

    private static OutboundMessage TextMessage(string to, string text) =>
        new(
            new ChannelAddress(ChannelType.Messenger, to),
            new MessageEnvelope([new TextBlock(text)]),
            new TenantId("tenant-a"),
            EntityId.From("conv-1"),
            null);

    // ── Send text ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldSendTextMessage_AndReturnSuccess()
    {
        var (connector, handler) = CreateConnector();

        var result = await connector.SendAsync(
            TextMessage("user-456", "Hello Messenger!"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalMessageId.Should().Be("m_sent001");

        var sentBody = await handler.LastRequestBody!.ReadAsStringAsync();
        sentBody.Should().Contain("\"text\":\"Hello Messenger!\"");
        sentBody.Should().NotContain("\"attachment\"");
    }

    // ── Send image ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldSendImageAttachment_WhenImageBlockProvided()
    {
        var (connector, handler) = CreateConnector();

        var message = new OutboundMessage(
            new ChannelAddress(ChannelType.Messenger, "user-456"),
            new MessageEnvelope([new ImageBlock("https://example.com/img.png", null, null)]),
            new TenantId("tenant-a"),
            EntityId.From("conv-1"),
            null);

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeTrue();

        var sentBody = await handler.LastRequestBody!.ReadAsStringAsync();
        sentBody.Should().Contain("\"type\":\"image\"");
        sentBody.Should().Contain("https://example.com/img.png");
    }

    // ── API error ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenApiReturnsError()
    {
        var errorResponse = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""
                {
                  "error": {
                    "message": "Invalid OAuth access token.",
                    "type": "OAuthException",
                    "code": 190,
                    "fbtrace_id": "abc123"
                  }
                }
                """, Encoding.UTF8, "application/json"),
        };

        var (connector, _) = CreateConnector(errorResponse);

        var result = await connector.SendAsync(TextMessage("user-456", "Hi"), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("190");
        result.ErrorMessage.Should().Contain("Invalid OAuth access token.");
    }

    // ── Request URL format ────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldPostToCorrectUrl()
    {
        var (connector, handler) = CreateConnector();

        await connector.SendAsync(TextMessage("user-456", "test"), CancellationToken.None);

        handler.LastRequestUri.Should().Be("https://graph.facebook.com/v21.0/me/messages");
    }

    // ── Channel property ──────────────────────────────────────────────────────

    [Fact]
    public void Channel_ShouldBeMessenger()
    {
        var (connector, _) = CreateConnector();
        connector.Channel.Should().Be(ChannelType.Messenger);
    }

    // ── GetStatusAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_ShouldReturnNull_Always()
    {
        var (connector, _) = CreateConnector();

        var status = await connector.GetStatusAsync("m_any", CancellationToken.None);

        status.Should().BeNull();
    }
}

/// <summary>Captures outgoing HTTP requests for assertion in tests.</summary>
internal sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
{
    public string? LastRequestUri { get; private set; }
    public FakeHttpContent? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequestBody = new FakeHttpContent(body);
        }
        return response;
    }
}

internal sealed class FakeHttpContent(string body) : HttpContent
{
    private readonly string _body = body;

    public new Task<string> ReadAsStringAsync() => Task.FromResult(_body);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new NotSupportedException();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
