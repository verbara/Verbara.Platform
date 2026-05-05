using System.Net;
using System.Text;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Twitter;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Channels.Twitter.Tests;

public class TwitterConnectorTests
{
    private const string BearerToken = "my-bearer-token";
    private const string BaseUrl = "https://api.x.com";

    private static TwitterOptions DefaultOptions() => new()
    {
        ApiKey = "api-key",
        ApiSecret = "api-secret",
        AccessToken = "access-token",
        AccessTokenSecret = "access-token-secret",
        BearerToken = BearerToken,
        BaseUrl = BaseUrl,
    };

    private static (TwitterConnector connector, FakeTwitterHttpHandler handler) CreateConnector(
        HttpResponseMessage? response = null,
        TwitterOptions? options = null)
    {
        var fakeHandler = new FakeTwitterHttpHandler(
            response ?? new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    """{ "dm_conversation_id": "conv-1", "dm_event_id": "evt-999" }""",
                    Encoding.UTF8,
                    "application/json"),
            });

        var httpClient = new HttpClient(fakeHandler);
        var connector = new TwitterConnector(
            httpClient,
            Options.Create(options ?? DefaultOptions()),
            NullLogger<TwitterConnector>.Instance);

        return (connector, fakeHandler);
    }

    private static OutboundMessage MakeMessage(string participantId, MessageBlock block) =>
        new(
            new ChannelAddress(ChannelType.Twitter, participantId),
            new MessageEnvelope([block]),
            new TenantId("tenant-a"),
            EntityId.From("conv-1"));

    // ── Send text DM ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldSendTextDm_WhenTextBlockProvided()
    {
        var (connector, handler) = CreateConnector();

        var result = await connector.SendAsync(
            MakeMessage("user-456", new TextBlock("Hello from X!")),
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalMessageId.Should().Be("evt-999");
        handler.LastRequestUri.Should().Contain("/dm_conversations/with/user-456/messages");
        handler.LastRequestBodyString.Should().Contain("\"text\":\"Hello from X!\"");
    }

    // ── Send DM uses correct URL with participant id ───────────────────────────

    [Fact]
    public async Task SendAsync_ShouldPostToCorrectUrl_WithParticipantId()
    {
        var (connector, handler) = CreateConnector();

        await connector.SendAsync(
            MakeMessage("user-789", new TextBlock("Hi")),
            CancellationToken.None);

        handler.LastRequestUri.Should().Be($"{BaseUrl}/2/dm_conversations/with/user-789/messages");
    }

    // ── Send DM sets Bearer authorization header ──────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldSetBearerAuthorizationHeader()
    {
        var (connector, handler) = CreateConnector();

        await connector.SendAsync(
            MakeMessage("user-111", new TextBlock("test")),
            CancellationToken.None);

        handler.LastAuthorizationHeader.Should().Be($"Bearer {BearerToken}");
    }

    // ── API error response ────────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenApiReturnsError()
    {
        var errorResponse = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                """{ "errors": [{ "message": "Forbidden", "code": 87 }] }""",
                Encoding.UTF8,
                "application/json"),
        };

        var (connector, _) = CreateConnector(errorResponse);

        var result = await connector.SendAsync(
            MakeMessage("user-999", new TextBlock("Hi")),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("403");
    }

    // ── GetStatusAsync always returns null ────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_ShouldReturnNull_Always()
    {
        var (connector, _) = CreateConnector();

        var status = await connector.GetStatusAsync("evt-123", CancellationToken.None);

        status.Should().BeNull();
    }

    // ── Channel property ──────────────────────────────────────────────────────

    [Fact]
    public void Channel_ShouldBeTwitter()
    {
        var (connector, _) = CreateConnector();

        connector.Channel.Should().Be(ChannelType.Twitter);
    }

    // ── Unsupported block type returns failure ────────────────────────────────

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenAudioBlockProvided()
    {
        var (connector, _) = CreateConnector();

        var result = await connector.SendAsync(
            MakeMessage("user-555", new AudioBlock("https://example.com/audio.mp3", TimeSpan.FromSeconds(30), "audio/mpeg")),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("UNSUPPORTED_BLOCK");
    }
}

/// <summary>Captures outgoing HTTP requests for assertion in tests.</summary>
internal sealed class FakeTwitterHttpHandler(HttpResponseMessage response) : HttpMessageHandler
{
    public string? LastRequestBodyString { get; private set; }
    public string? LastRequestUri { get; private set; }
    public string? LastAuthorizationHeader { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        if (request.Content is not null)
            LastRequestBodyString = await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.Headers.TryGetValues("Authorization", out var values))
            LastAuthorizationHeader = string.Join("", values);
        return response;
    }
}
