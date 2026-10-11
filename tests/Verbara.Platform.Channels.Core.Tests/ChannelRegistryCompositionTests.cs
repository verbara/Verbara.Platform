using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Core;

namespace Verbara.Platform.Channels.Core.Tests;

/// <summary>
/// whatsapp-works-for-real D1 — the registry is composed from the handlers and connectors that modules
/// register in DI, lazily, instead of imperative RegisterHandler/RegisterConnector calls nobody made.
/// </summary>
public sealed class ChannelRegistryCompositionTests
{
    private static IWebhookHandler MakeHandler(ChannelType channel)
    {
        var handler = Substitute.For<IWebhookHandler>();
        handler.Channel.Returns(channel);
        return handler;
    }

    private static IChannelConnector MakeConnector(ChannelType channel)
    {
        var connector = Substitute.For<IChannelConnector>();
        connector.Channel.Returns(channel);
        return connector;
    }

    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddPlatformChannels();
        return services;
    }

    [Fact]
    public void AddPlatformChannels_ShouldExposeHandlers_WhenModulesRegisterThem()
    {
        var handler = MakeHandler(ChannelType.WhatsApp);
        var connector = MakeConnector(ChannelType.WhatsApp);
        var services = BaseServices();
        services.AddSingleton(handler);
        services.AddSingleton(connector);

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelRegistry>();

        registry.GetHandler(ChannelType.WhatsApp).Should().BeSameAs(handler);
        registry.GetConnector(ChannelType.WhatsApp).Should().BeSameAs(connector);
    }

    [Fact]
    public void AddPlatformChannels_ShouldNotEnumerateRegistrations_WhenRegistryIsOnlyResolved()
    {
        // Defer-seams rule: resolving the registry (as a singleton consumer does at host start) must not
        // build any connector; the composition happens on first use.
        var built = 0;
        var services = BaseServices();
        services.AddTransient<IChannelConnector>(_ =>
        {
            built++;
            return MakeConnector(ChannelType.WhatsApp);
        });

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelRegistry>();

        built.Should().Be(0);
        registry.AvailableChannels.Should().Equal(ChannelType.WhatsApp);
        built.Should().Be(1);
    }

    [Fact]
    public void AddPlatformChannels_ShouldExposeNoChannel_WhenNoModuleRegistersOne()
    {
        using var provider = BaseServices().BuildServiceProvider();
        var registry = provider.GetRequiredService<IChannelRegistry>();

        registry.WebhookChannels.Should().BeEmpty();
        registry.AvailableChannels.Should().BeEmpty();
    }
}
