using System.Text;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Channels.Video;
using Asterisk.Platform.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asterisk.Platform.Channels.Video.Tests;

public class VideoWebhookHandlerTests
{
    private static readonly TenantId TenantA = new("tenant-a");

    private static VideoWebhookHandler CreateHandler() =>
        new(NullLogger<VideoWebhookHandler>.Instance);

    private static ReadOnlyMemory<byte> ToBody(string json) =>
        new(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenSessionStartedEventReceived()
    {
        var handler = CreateHandler();
        var json = """{"event":"session.started","session_id":"s-001","timestamp":1700000000}""";

        var result = await handler.HandleAsync(ToBody(json), new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenSessionEndedEventReceived()
    {
        var handler = CreateHandler();
        var json = """{"event":"session.ended","session_id":"s-001","timestamp":1700000000}""";

        var result = await handler.HandleAsync(ToBody(json), new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenParticipantJoinedEventReceived()
    {
        var handler = CreateHandler();
        var json = """{"event":"participant.joined","session_id":"s-001","participant_id":"p-1","timestamp":1700000000}""";

        var result = await handler.HandleAsync(ToBody(json), new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenParticipantLeftEventReceived()
    {
        var handler = CreateHandler();
        var json = """{"event":"participant.left","session_id":"s-001","participant_id":"p-1","timestamp":1700000000}""";

        var result = await handler.HandleAsync(ToBody(json), new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenPayloadIsInvalidJson()
    {
        var handler = CreateHandler();
        var body = ToBody("not-json{{{");

        var result = await handler.HandleAsync(body, new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenEventFieldIsMissing()
    {
        var handler = CreateHandler();
        var json = """{"session_id":"s-001","timestamp":1700000000}""";

        var result = await handler.HandleAsync(ToBody(json), new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }

    [Fact]
    public async Task HandleAsync_ShouldReturnIgnored_WhenUnknownEventType()
    {
        var handler = CreateHandler();
        var json = """{"event":"unknown.event","session_id":"s-001","timestamp":1700000000}""";

        var result = await handler.HandleAsync(ToBody(json), new Dictionary<string, string>(), TenantA, CancellationToken.None);

        result.Type.Should().Be(WebhookResultType.Ignored);
    }
}
