using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Sms;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Verbara.Platform.Channels.Sms.Tests;

public class SmsConnectorTests
{
    private static SmsConnector CreateConnector(
        ISmsProvider? provider = null,
        string from = "+15550001111",
        int maxSegments = 3)
    {
        provider ??= Substitute.For<ISmsProvider>();
        var options = Options.Create(new SmsOptions { DefaultFromNumber = from, MaxSegments = maxSegments });
        return new SmsConnector(provider, options);
    }

    private static OutboundMessage MakeMessage(MessageEnvelope content, string to = "+15559998888") =>
        new(new ChannelAddress(ChannelType.Sms, to), content, new TenantId("test-tenant"), EntityId.New());

    [Fact]
    public void Channel_ShouldBeSms()
    {
        var connector = CreateConnector();

        connector.Channel.Should().Be(ChannelType.Sms);
    }

    [Fact]
    public async Task SendAsync_ShouldDelegateToProvider_WhenEnvelopeContainsTextBlock()
    {
        var provider = Substitute.For<ISmsProvider>();
        provider.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new SmsSendResult(true, "msg-123", null, null));
        var connector = CreateConnector(provider, from: "+15550001111");
        var message = MakeMessage(new MessageEnvelope([new TextBlock("Hello SMS")]), "+15559998888");

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalMessageId.Should().Be("msg-123");
        await provider.Received(1).SendAsync("+15550001111", "+15559998888", "Hello SMS", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldExtractTextFromEnvelope_WhenMultipleTextBlocksPresent()
    {
        var provider = Substitute.For<ISmsProvider>();
        provider.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new SmsSendResult(true, "msg-456", null, null));
        var connector = CreateConnector(provider);
        var envelope = new MessageEnvelope([new TextBlock("Hello"), new TextBlock("World")]);
        var message = MakeMessage(envelope);

        await connector.SendAsync(message, CancellationToken.None);

        await provider.Received(1).SendAsync(
            Arg.Any<string>(), Arg.Any<string>(), "Hello World", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenEnvelopeContainsOnlyNonTextBlocks()
    {
        var provider = Substitute.For<ISmsProvider>();
        var connector = CreateConnector(provider);
        var envelope = new MessageEnvelope([new ImageBlock("https://example.com/img.jpg", null, null)]);
        var message = MakeMessage(envelope);

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("UNSUPPORTED_CONTENT");
        await provider.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldSkipNonTextBlocksAndSendText_WhenMixedContent()
    {
        var provider = Substitute.For<ISmsProvider>();
        provider.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new SmsSendResult(true, "msg-789", null, null));
        var connector = CreateConnector(provider);
        var envelope = new MessageEnvelope(
        [
            new ImageBlock("https://example.com/img.jpg", null, null),
            new TextBlock("Please see the image above"),
            new AudioBlock("https://example.com/audio.mp3", null, null),
        ]);
        var message = MakeMessage(envelope);

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeTrue();
        await provider.Received(1).SendAsync(
            Arg.Any<string>(), Arg.Any<string>(), "Please see the image above", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldTruncateBody_WhenTextExceedsMaxSegments()
    {
        var provider = Substitute.For<ISmsProvider>();
        provider.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new SmsSendResult(true, "msg-trunc", null, null));
        // maxSegments=1 → only 160 chars allowed for GSM-7
        var connector = CreateConnector(provider, maxSegments: 1);
        var longText = new string('A', 200); // 200 GSM-7 chars = 2 segments
        var message = MakeMessage(new MessageEnvelope([new TextBlock(longText)]));

        await connector.SendAsync(message, CancellationToken.None);

        await provider.Received(1).SendAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<string>(s => s != null && s.Length == 160),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetStatusAsync_ShouldReturnMappedStatus_WhenProviderReturnsDelivered()
    {
        var provider = Substitute.For<ISmsProvider>();
        provider.GetStatusAsync("msg-123", Arg.Any<CancellationToken>())
                .Returns(SmsDeliveryStatus.Delivered);
        var connector = CreateConnector(provider);

        var status = await connector.GetStatusAsync("msg-123", CancellationToken.None);

        status.Should().Be(MessageDeliveryStatus.Delivered);
    }
}
