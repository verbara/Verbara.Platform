using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.Telegram;

/// <summary>
/// DI registration extensions for Platform.Channels.Telegram services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Telegram connector, webhook handler, and message transformer.
    /// </summary>
    public static IServiceCollection AddTelegram(
        this IServiceCollection services,
        Action<TelegramOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<TelegramConnector>();
        services.AddSingleton<TelegramWebhookHandler>();
        services.AddSingleton<TelegramMessageTransformer>();

        return services;
    }
}
