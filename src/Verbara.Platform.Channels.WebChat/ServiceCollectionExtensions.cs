using Verbara.Platform.Channels.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.WebChat;

/// <summary>
/// DI registration extensions for Platform.Channels.WebChat services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WebChat connector, session manager, and message adapter. The connector is also
    /// exposed as an <see cref="IChannelConnector"/>, which is what makes WebChat reachable through
    /// the channel registry for agent replies.
    /// </summary>
    public static IServiceCollection AddWebChat(
        this IServiceCollection services,
        Action<WebChatOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<WebChatOptions>();

        services.AddSingleton<WebChatSessionManager>();
        services.AddSingleton<WebSocketWebChatTransport>();
        services.AddSingleton<IWebChatTransport>(sp => sp.GetRequiredService<WebSocketWebChatTransport>());
        services.AddSingleton<WebChatConnector>();
        services.AddSingleton<IChannelConnector>(sp => sp.GetRequiredService<WebChatConnector>());

        return services;
    }
}
