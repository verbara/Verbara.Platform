using Verbara.Platform.Channels.Sms.Providers;
using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.Sms;

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

        // CSAT correlator (csat-runner Phase D). Plugs into the inbound SMS path after
        // SmsWebhookHandler to capture bare 1..5 digit replies against an open
        // csat_pending_dispatches row, falling through to normal routing otherwise. Depends on the
        // host-provided NpgsqlDataSource + ICsatCaptureForwarder (registered in the Api composition
        // root); registered here so any host wiring AddSms picks the correlator up.
        services.AddSingleton<CsatSmsCorrelator>();

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
