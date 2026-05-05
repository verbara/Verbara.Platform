using System.Net;
using System.Text;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Instagram;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Channels.Instagram.Tests;

public class InstagramConnectorTests
{
    private const string AccountId = "ig-account-123";
    private const string PageAccessToken = "EAAtest";

    private static InstagramOptions DefaultOptions() => new()
    {
        InstagramAccountId = AccountId,
        PageAccessToken = PageAccessToken,
        WebhookVerifyToken = "token",
        AppSecret = "secret",
        ApiVersion = "v21.0",
        BaseUrl = "https://graph.facebook.com",
    };

    private static (InstagramConnector connector, FakeHttpMessageHandler handler) CreateConnector(
        HttpResponseMessage? response = null,
        InstagramOptions? options = null)
    {
        var fakeHandler = new FakeHttpMessageHandler(
            response ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    { "recipient_id": "iguser-789", "message_id": "ig_m_sent001" }
                    """, Encoding.UTF8, "application/json"),
            });

        var httpClient = new HttpClient(fakeHandler);
        var connector = new InstagramConnector(
            httpClient,
            Options.Create(options ?? DefaultOptions()),
            NullLogger<InstagramConnector>.Instance);

        return (connector, fakeHandler);
    }

    private static OutboundMessage TextMessage(string to, string text) =>
        new(
            new ChannelAddress(ChannelType.Instagram, to),
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
            TextMessage("iguser-789", "Hello Instagram!"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalMessageId.Should().Be("ig_m_sent001");

        var sentBody = await handler.LastRequestBody!.ReadAsStringAsync();
        sentBody.Should().Contain("\"text\":\"Hello Instagram!\"");
    }

    // ── Send image ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldSendImageAttachment_WhenImageBlockProvided()
    {
        var (connector, handler) = CreateConnector();

        var message = new OutboundMessage(
            new ChannelAddress(ChannelType.Instagram, "iguser-789"),
            new MessageEnvelope([new ImageBlock("https://example.com/ig.jpg", null, null)]),
            new TenantId("tenant-a"),
            EntityId.From("conv-1"),
            null);

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeTrue();

        var sentBody = await handler.LastRequestBody!.ReadAsStringAsync();
        sentBody.Should().Contain("\"type\":\"image\"");
        sentBody.Should().Contain("https://example.com/ig.jpg");
    }

    // ── Request URL format ────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldPostToCorrectUrl()
    {
        var (connector, handler) = CreateConnector();

        await connector.SendAsync(TextMessage("iguser-789", "test"), CancellationToken.None);

        handler.LastRequestUri.Should().Be("https://graph.facebook.com/v21.0/me/messages");
    }

    // ── Channel property ──────────────────────────────────────────────────────

    [Fact]
    public void Channel_ShouldBeInstagram()
    {
        var (connector, _) = CreateConnector();
        connector.Channel.Should().Be(ChannelType.Instagram);
    }

    // ── GetStatusAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_ShouldReturnNull_Always()
    {
        var (connector, _) = CreateConnector();

        var status = await connector.GetStatusAsync("ig_m_any", CancellationToken.None);

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
