using Verbara.Platform.Channels.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.WebChat;

/// <summary>
/// DI registration extensions for Platform.Channels.WebChat services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WebChat connector (also exposed as <see cref="IChannelConnector"/> for the channel
    /// registry), session manager, and message adapter.
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
        // Expose the outbound connector to the channel registry (whatsapp-works-for-real D1/D2): an agent
        // reply to a WebChat contact reaches this connector instead of "no connector registered". WebChat
        // has no webhook handler — its inbound path is its own endpoints.
        services.AddSingleton<IChannelConnector>(sp => sp.GetRequiredService<WebChatConnector>());

        return services;
    }
}
