using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.WebChat;

/// <summary>
/// DI registration extensions for Platform.Channels.WebChat services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers WebChat connector, session manager, and message adapter.
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

        return services;
    }
}
