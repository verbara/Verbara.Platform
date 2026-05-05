using Verbara.Sdk.Resilience;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.Rcs;

/// <summary>
/// DI registration extensions for Platform.Channels.Rcs services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the RCS connector, webhook handler, message transformer, and the
    /// keyed <see cref="ResiliencePolicy"/> consumed by <see cref="RcsConnector"/>
    /// to wrap <see cref="IRcsProvider"/> calls (circuit 5/60s + retry 2/500ms + timeout 15s).
    /// An <see cref="IRcsProvider"/> must be registered separately.
    /// </summary>
    public static IServiceCollection AddRcs(
        this IServiceCollection services,
        Action<RcsOptions> configure)
    {
        services.Configure(configure);

        services.AddSingleton<RcsConnector>();
        services.AddSingleton<RcsWebhookHandler>();
        services.AddSingleton<RcsMessageTransformer>();

        services.AddKeyedSingleton<ResiliencePolicy>(
            RcsConnector.ResiliencePolicyKey,
            (_, _) => new ResiliencePolicyBuilder()
                .WithCircuitBreaker(threshold: 5, openDuration: TimeSpan.FromSeconds(60))
                .WithRetry(maxAttempts: 2, baseDelay: TimeSpan.FromMilliseconds(500))
                .WithTimeout(TimeSpan.FromSeconds(15))
                .Build());

        return services;
    }
}
