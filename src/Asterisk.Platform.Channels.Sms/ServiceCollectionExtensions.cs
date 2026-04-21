using Asterisk.Platform.Channels.Sms.Providers;
using Asterisk.Sdk.Resilience;
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

    /// <summary>
    /// Registers the keyed <see cref="ResiliencePolicy"/> consumed by <see cref="TwilioSmsProvider"/>
    /// to wrap outbound Twilio HTTP calls (circuit 5/30s + retry 3/200ms + timeout 10s).
    /// Call this alongside the Twilio provider DI wiring when Twilio is the active SMS provider.
    /// </summary>
    public static IServiceCollection AddTwilioResiliencePolicy(this IServiceCollection services)
    {
        services.AddKeyedSingleton<ResiliencePolicy>(
            TwilioSmsProvider.ResiliencePolicyKey,
            (_, _) => new ResiliencePolicyBuilder()
                .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(30))
                .WithRetry(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(200))
                .WithTimeout(TimeSpan.FromSeconds(10))
                .Build());
        return services;
    }
}
