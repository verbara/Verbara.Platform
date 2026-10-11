using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using NSubstitute;

namespace Verbara.Platform.Channels.Core.Tests;

public class ChannelRegistryTests
{
    private static ChannelRegistry CreateRegistry(
        IEnumerable<IWebhookHandler>? handlers = null,
        IEnumerable<IChannelConnector>? connectors = null) =>
        new(handlers ?? [], connectors ?? []);

    private static IChannelConnector MakeConnector(ChannelType channel)
    {
        var connector = Substitute.For<IChannelConnector>();
        connector.Channel.Returns(channel);
        return connector;
    }

    private static IWebhookHandler MakeHandler(ChannelType channel)
    {
        var handler = Substitute.For<IWebhookHandler>();
        handler.Channel.Returns(channel);
        return handler;
    }

    private static ChannelConstraints MakeConstraints() =>
        new(MaxMessageLength: 1600, SessionWindow: TimeSpan.FromHours(24),
            SupportsRichMedia: true, SupportsInteractive: true,
            RequiresTemplateOutsideWindow: true, MaxMediaSizeMb: 16);

    [Fact]
    public void GetConnector_ShouldReturnRegisteredConnector_WhenChannelIsRegistered()
    {
        var connector = MakeConnector(ChannelType.WhatsApp);
        var registry = CreateRegistry(connectors: [connector]);

        var result = registry.GetConnector(ChannelType.WhatsApp);

        result.Should().BeSameAs(connector);
    }

    [Fact]
    public void GetConnector_ShouldThrow_WhenChannelIsNotRegistered()
    {
        var registry = CreateRegistry();

        var act = () => registry.GetConnector(ChannelType.Sms);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Sms*");
    }

    [Fact]
    public void GetHandler_ShouldReturnRegisteredHandler_WhenChannelIsRegistered()
    {
        var handler = MakeHandler(ChannelType.WhatsApp);
        var registry = CreateRegistry(handlers: [handler]);

        var result = registry.GetHandler(ChannelType.WhatsApp);

        result.Should().BeSameAs(handler);
    }

    [Fact]
    public void GetHandler_ShouldThrow_WhenChannelIsNotRegistered()
    {
        var registry = CreateRegistry();

        var act = () => registry.GetHandler(ChannelType.Email);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Email*");
    }

    [Fact]
    public void GetConstraints_ShouldReturnRegisteredConstraints_WhenChannelIsRegistered()
    {
        var registry = CreateRegistry();
        var constraints = MakeConstraints();
        registry.RegisterConstraints(ChannelType.WhatsApp, constraints);

        var result = registry.GetConstraints(ChannelType.WhatsApp);

        result.Should().BeSameAs(constraints);
    }

    [Fact]
    public void GetConstraints_ShouldThrow_WhenChannelIsNotRegistered()
    {
        var registry = CreateRegistry();

        var act = () => registry.GetConstraints(ChannelType.WebChat);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*WebChat*");
    }

    [Fact]
    public void AvailableChannels_ShouldListAllRegisteredConnectorChannels()
    {
        var registry = CreateRegistry(connectors: [MakeConnector(ChannelType.WhatsApp), MakeConnector(ChannelType.Sms)]);

        registry.AvailableChannels.Should().BeEquivalentTo([ChannelType.WhatsApp, ChannelType.Sms]);
    }

    [Fact]
    public void AvailableChannels_ShouldBeEmpty_WhenNoConnectorsRegistered()
    {
        var registry = CreateRegistry();

        registry.AvailableChannels.Should().BeEmpty();
    }

    [Fact]
    public void GetConnector_ShouldThrow_WhenSameChannelRegisteredTwice()
    {
        var registry = CreateRegistry(connectors: [MakeConnector(ChannelType.WhatsApp), MakeConnector(ChannelType.WhatsApp)]);

        var act = () => registry.GetConnector(ChannelType.WhatsApp);

        act.Should().Throw<InvalidOperationException>().WithMessage("*More than one connector*WhatsApp*");
    }

    [Fact]
    public void GetHandler_ShouldThrow_WhenSameChannelRegisteredTwice()
    {
        var registry = CreateRegistry(handlers: [MakeHandler(ChannelType.WhatsApp), MakeHandler(ChannelType.WhatsApp)]);

        var act = () => registry.GetHandler(ChannelType.WhatsApp);

        act.Should().Throw<InvalidOperationException>().WithMessage("*More than one webhook handler*WhatsApp*");
    }

    [Fact]
    public void TryGetHandler_ShouldReturnFalse_WhenChannelIsNotRegistered()
    {
        var registry = CreateRegistry(handlers: [MakeHandler(ChannelType.WhatsApp)]);

        registry.TryGetHandler(ChannelType.Sms, out var handler).Should().BeFalse();
        handler.Should().BeNull();
        registry.WebhookChannels.Should().Equal(ChannelType.WhatsApp);
    }
}
