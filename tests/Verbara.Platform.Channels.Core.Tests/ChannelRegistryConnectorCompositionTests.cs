using Verbara.Platform.Channels.Core;
using Verbara.Platform.Core;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Verbara.Platform.Channels.Core.Tests;

/// <summary>
/// The DI-built registry exposes the connectors the channel modules registered as
/// <see cref="IChannelConnector"/>, resolved on first use rather than when the registry is built.
/// </summary>
public class ChannelRegistryConnectorCompositionTests
{
    private static IChannelConnector MakeConnector(ChannelType channel)
    {
        var connector = Substitute.For<IChannelConnector>();
        connector.Channel.Returns(channel);
        return connector;
    }

    [Fact]
    public void GetConnector_ShouldReturnDiRegisteredConnector_WhenModuleRegisteredIt()
    {
        var webChat = MakeConnector(ChannelType.WebChat);
        var services = new ServiceCollection();
        services.AddPlatformChannels();
        services.AddSingleton(webChat);
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IChannelRegistry>();

        registry.GetConnector(ChannelType.WebChat).Should().BeSameAs(webChat);
        registry.AvailableChannels.Should().Equal(ChannelType.WebChat);
    }

    [Fact]
    public void AddPlatformChannels_ShouldNotResolveConnectors_WhenRegistryIsResolved()
    {
        var resolutions = 0;
        var services = new ServiceCollection();
        services.AddPlatformChannels();
        services.AddSingleton<IChannelConnector>(_ =>
        {
            resolutions++;
            return MakeConnector(ChannelType.WhatsApp);
        });
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IChannelRegistry>();
        resolutions.Should().Be(0, "a connector is an external seam and must not resolve at host start");

        registry.GetConnector(ChannelType.WhatsApp);
        registry.GetConnector(ChannelType.WhatsApp);
        resolutions.Should().Be(1);
    }

    [Fact]
    public void GetConnector_ShouldThrow_WhenTwoModulesRegisterTheSameChannel()
    {
        // Integration of a-in + a-out: the registry has no imperative RegisterConnector (D1), so a second
        // connector for a channel is a composition error rather than an override.
        var services = new ServiceCollection();
        services.AddPlatformChannels();
        services.AddSingleton(MakeConnector(ChannelType.WebChat));
        services.AddSingleton(MakeConnector(ChannelType.WebChat));
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ChannelRegistry>();

        var act = () => registry.GetConnector(ChannelType.WebChat);

        act.Should().Throw<InvalidOperationException>().WithMessage("*More than one connector*WebChat*");
    }

    [Fact]
    public void GetConnector_ShouldThrow_WhenNoModuleRegisteredTheChannel()
    {
        var services = new ServiceCollection();
        services.AddPlatformChannels();
        services.AddSingleton(MakeConnector(ChannelType.WebChat));
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelRegistry>();

        var act = () => registry.GetConnector(ChannelType.Sms);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Sms*");
    }
}
