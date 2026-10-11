using Verbara.Platform.Channels.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.Core;

/// <summary>
/// DI registration extensions for Platform.Channels.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the channel registry, inbound message pipeline, and delivery status handler. The registry
    /// serves exactly the <see cref="IWebhookHandler"/> and <see cref="IChannelConnector"/> services that
    /// channel modules register, enumerated on first use (whatsapp-works-for-real D1).
    /// </summary>
    public static IServiceCollection AddPlatformChannels(this IServiceCollection services)
    {
        services.AddSingleton(sp => new ChannelRegistry(
            () => sp.GetServices<IWebhookHandler>(),
            () => sp.GetServices<IChannelConnector>()));
        services.AddSingleton<IChannelRegistry>(sp => sp.GetRequiredService<ChannelRegistry>());

        services.AddSingleton<DeliveryStatusHandler>();

        services.AddTransient<IInboundMessagePipeline, InboundMessagePipeline>();

        return services;
    }
}
