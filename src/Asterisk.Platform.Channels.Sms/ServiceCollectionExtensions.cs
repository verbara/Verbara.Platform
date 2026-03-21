using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.Sms;

/// <summary>
/// DI registration extensions for Platform.Channels.Sms services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SMS connector and webhook handler.
    /// </summary>
    public static IServiceCollection AddSms(
        this IServiceCollection services,
        Action<SmsOptions> configure)
    {
        services.Configure(configure);

        services.AddSingleton<SmsConnector>();
        services.AddSingleton<SmsWebhookHandler>();

        return services;
    }
}
