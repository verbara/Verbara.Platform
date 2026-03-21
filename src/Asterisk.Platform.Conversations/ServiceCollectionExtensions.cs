using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Conversations;

/// <summary>
/// DI registration extensions for Platform.Conversations services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers conversation services and configures <see cref="ConversationOptions"/>.
    /// </summary>
    public static IServiceCollection AddPlatformConversations(
        this IServiceCollection services,
        Action<ConversationOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.AddOptions<ConversationOptions>();

        return services;
    }
}
