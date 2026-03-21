using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Channels.Rcs;

/// <summary>
/// DI registration extensions for Platform.Channels.Rcs services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the RCS connector, webhook handler, and message transformer.
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

        return services;
    }
}
