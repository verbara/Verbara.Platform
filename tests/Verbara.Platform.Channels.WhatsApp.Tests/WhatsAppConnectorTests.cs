using System.Net;
using System.Text;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.WhatsApp;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Channels.WhatsApp.Tests;

public class WhatsAppConnectorTests
{
    private const string PhoneNumberId = "123456789";
    private const string AccessToken = "EAAtest";

    private static (WhatsAppConnector connector, FakeHttpMessageHandler handler) CreateConnector(
        HttpResponseMessage? response = null)
    {
        var fakeHandler = new FakeHttpMessageHandler(
            response ?? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "messaging_product": "whatsapp",
                      "contacts": [{ "input": "15550000002", "wa_id": "15550000002" }],
                      "messages": [{ "id": "wamid.sent001" }]
                    }
                    """, Encoding.UTF8, "application/json"),
            });

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(WhatsAppConnector.HttpClientName).Returns(_ => new HttpClient(fakeHandler, disposeHandler: false));

        var configStore = Substitute.For<ITenantChannelConfigStore>();
        configStore.GetAsync(Arg.Any<TenantId>(), ChannelType.WhatsApp, Arg.Any<CancellationToken>())
            .Returns(new TenantChannelConfig
            {
                TenantId = new TenantId("tenant-a"),
                Channel = ChannelType.WhatsApp,
                Credentials = new Dictionary<string, string>
                {
                    [WhatsAppCredentialKeys.AccessToken] = AccessToken,
                    [WhatsAppCredentialKeys.PhoneNumberId] = PhoneNumberId,
                },
            });

        var connector = new WhatsAppConnector(
            factory,
            configStore,
            Options.Create(new WhatsAppOptions
            {
                WebhookVerifyToken = "token",
                AppSecret = "secret",
                ApiVersion = "v21.0",
                BaseUrl = "https://graph.facebook.com",
            }),
            NullLogger<WhatsAppConnector>.Instance);

        return (connector, fakeHandler);
    }

    private static OutboundMessage TextMessage(string to, string text, string? templateId = null) =>
        new(
            new ChannelAddress(ChannelType.WhatsApp, to),
            new MessageEnvelope([new TextBlock(text)]),
            new TenantId("tenant-a"),
            EntityId.From("conv-1"),
            templateId);

    // ── Send text (no template requested) ─────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldSendTextMessage_WhenNoTemplateRequested()
    {
        var (connector, handler) = CreateConnector();

        var result = await connector.SendAsync(
            TextMessage("15550000002", "Hi there!"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalMessageId.Should().Be("wamid.sent001");

        var sentBody = await handler.LastRequestBody!.ReadAsStringAsync();
        sentBody.Should().Contain("\"type\":\"text\"");
        sentBody.Should().Contain("\"body\":\"Hi there!\"");
        sentBody.Should().NotContain("\"type\":\"template\"");
    }

    // ── Send with template (only when requested) ──────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldUseTemplate_WhenExplicitTemplateIdProvided()
    {
        var (connector, handler) = CreateConnector();

        var result = await connector.SendAsync(
            TextMessage("15550000004", "Hello!", templateId: "promo_template"),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        var sentBody = await handler.LastRequestBody!.ReadAsStringAsync();
        sentBody.Should().Contain("\"type\":\"template\"");
        sentBody.Should().Contain("\"name\":\"promo_template\"");
    }

    // ── API error response ────────────────────────────────────────────────────

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

        var result = await connector.SendAsync(
            TextMessage("15550000005", "Hello"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("190");
        result.ErrorMessage.Should().Contain("Invalid OAuth access token.");
    }

    // ── Request URL format ────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldPostToCorrectUrl()
    {
        var (connector, handler) = CreateConnector();

        await connector.SendAsync(TextMessage("15550000006", "test"), CancellationToken.None);

        var expectedUrl = $"https://graph.facebook.com/v21.0/{PhoneNumberId}/messages";
        handler.LastRequestUri.Should().Be(expectedUrl);
    }

    // ── GetStatusAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_ShouldReturnNull_Always()
    {
        var (connector, _) = CreateConnector();

        var status = await connector.GetStatusAsync("wamid.any", CancellationToken.None);

        status.Should().BeNull();
    }

    // ── Channel property ──────────────────────────────────────────────────────

    [Fact]
    public void Channel_ShouldBeWhatsApp()
    {
        var (connector, _) = CreateConnector();
        connector.Channel.Should().Be(ChannelType.WhatsApp);
    }
}

/// <summary>Captures outgoing HTTP requests for assertion in tests.</summary>
internal sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
{
    public string? LastRequestBodyString { get; private set; }
    public string? LastRequestUri { get; private set; }

    // Expose for backward-compat; reads the buffered string content
    public FakeHttpContent? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        if (request.Content is not null)
        {
            LastRequestBodyString = await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequestBody = new FakeHttpContent(LastRequestBodyString);
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
