using Verbara.Platform.Channels.Core;
using Verbara.Platform.Channels.Rcs;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using NSubstitute;

namespace Verbara.Platform.Channels.Rcs.Tests;

public class RcsConnectorTests
{
    private static RcsConnector CreateConnector(IRcsProvider? provider = null)
    {
        provider ??= Substitute.For<IRcsProvider>();
        return new RcsConnector(provider);
    }

    private static OutboundMessage MakeMessage(MessageEnvelope content, string to = "+15559998888") =>
        new(new ChannelAddress(ChannelType.Rcs, to), content, new TenantId("test-tenant"), EntityId.New());

    private static IRcsProvider RcsEnabledProvider(string messageId = "rcs-msg-1")
    {
        var provider = Substitute.For<IRcsProvider>();
        provider.CheckCapabilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new RcsCapability(true, ["richCard", "carousel"]));
        provider.SendMessageAsync(Arg.Any<string>(), Arg.Any<RcsMessage>(), Arg.Any<CancellationToken>())
                .Returns(new RcsSendResult(true, messageId, null));
        return provider;
    }

    private static IRcsProvider RcsDisabledProvider()
    {
        var provider = Substitute.For<IRcsProvider>();
        provider.CheckCapabilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new RcsCapability(false, []));
        return provider;
    }

    [Fact]
    public void Channel_ShouldBeRcs()
    {
        var connector = CreateConnector();

        connector.Channel.Should().Be(ChannelType.Rcs);
    }

    [Fact]
    public async Task SendAsync_ShouldCheckCapabilityBeforeSending()
    {
        var provider = RcsEnabledProvider();
        var connector = CreateConnector(provider);
        var message = MakeMessage(new MessageEnvelope([new TextBlock("Hello RCS")]), "+15551234567");

        await connector.SendAsync(message, CancellationToken.None);

        await provider.Received(1).CheckCapabilityAsync("+15551234567", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldSendTextMessage_WhenRecipientIsRcsEnabled()
    {
        var provider = RcsEnabledProvider("rcs-abc");
        var connector = CreateConnector(provider);
        var message = MakeMessage(new MessageEnvelope([new TextBlock("Hello RCS")]));

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.ExternalMessageId.Should().Be("rcs-abc");
        await provider.Received(1).SendMessageAsync(
            Arg.Any<string>(),
            Arg.Is<RcsMessage>(m => m is RcsTextMessage),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenRecipientIsNotRcsEnabled()
    {
        var provider = RcsDisabledProvider();
        var connector = CreateConnector(provider);
        var message = MakeMessage(new MessageEnvelope([new TextBlock("Hello")]));

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("recipient_not_rcs_enabled");
        await provider.DidNotReceive().SendMessageAsync(
            Arg.Any<string>(), Arg.Any<RcsMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_ShouldReturnFailure_WhenEnvelopeIsEmpty()
    {
        var provider = RcsEnabledProvider();
        var connector = CreateConnector(provider);
        var message = MakeMessage(new MessageEnvelope([]));

        var result = await connector.SendAsync(message, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("UNSUPPORTED_CONTENT");
    }

    [Fact]
    public async Task GetStatusAsync_ShouldReturnDelivered_WhenProviderReturnsDelivered()
    {
        var provider = Substitute.For<IRcsProvider>();
        provider.GetStatusAsync("msg-1", Arg.Any<CancellationToken>())
                .Returns(RcsDeliveryStatus.Delivered);
        var connector = CreateConnector(provider);

        var status = await connector.GetStatusAsync("msg-1", CancellationToken.None);

        status.Should().Be(Verbara.Platform.Conversations.MessageDeliveryStatus.Delivered);
    }

    [Fact]
    public async Task GetStatusAsync_ShouldReturnDelivered_WhenProviderReturnsRead()
    {
        var provider = Substitute.For<IRcsProvider>();
        provider.GetStatusAsync("msg-2", Arg.Any<CancellationToken>())
                .Returns(RcsDeliveryStatus.Read);
        var connector = CreateConnector(provider);

        var status = await connector.GetStatusAsync("msg-2", CancellationToken.None);

        status.Should().Be(Verbara.Platform.Conversations.MessageDeliveryStatus.Delivered);
    }
}
