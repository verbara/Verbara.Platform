using Asterisk.Platform.Channels.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.Core;

/// <summary>
/// DI registration extensions for Platform.Channels.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the channel registry, inbound message pipeline, and delivery status handler.
    /// </summary>
    public static IServiceCollection AddPlatformChannels(this IServiceCollection services)
    {
        services.AddSingleton<ChannelRegistry>();
        services.AddSingleton<IChannelRegistry>(sp => sp.GetRequiredService<ChannelRegistry>());

        services.AddSingleton<DeliveryStatusHandler>();

        services.AddTransient<IInboundMessagePipeline, InboundMessagePipeline>();

        return services;
    }
}
