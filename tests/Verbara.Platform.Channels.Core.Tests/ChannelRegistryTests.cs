using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using NSubstitute;

namespace Verbara.Platform.Channels.Core.Tests;

public class ChannelRegistryTests
{
    private static ChannelRegistry CreateRegistry() => new();

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
        var registry = CreateRegistry();
        var connector = MakeConnector(ChannelType.WhatsApp);
        registry.RegisterConnector(connector);

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
        var registry = CreateRegistry();
        var handler = MakeHandler(ChannelType.WhatsApp);
        registry.RegisterHandler(handler);

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
        var registry = CreateRegistry();
        registry.RegisterConnector(MakeConnector(ChannelType.WhatsApp));
        registry.RegisterConnector(MakeConnector(ChannelType.Sms));

        registry.AvailableChannels.Should().BeEquivalentTo([ChannelType.WhatsApp, ChannelType.Sms]);
    }

    [Fact]
    public void AvailableChannels_ShouldBeEmpty_WhenNoConnectorsRegistered()
    {
        var registry = CreateRegistry();

        registry.AvailableChannels.Should().BeEmpty();
    }

    [Fact]
    public void RegisterConnector_ShouldReplaceExisting_WhenSameChannelRegisteredTwice()
    {
        var registry = CreateRegistry();
        var first = MakeConnector(ChannelType.WhatsApp);
        var second = MakeConnector(ChannelType.WhatsApp);
        registry.RegisterConnector(first);
        registry.RegisterConnector(second);

        var result = registry.GetConnector(ChannelType.WhatsApp);

        result.Should().BeSameAs(second);
        registry.AvailableChannels.Should().HaveCount(1);
    }
}
