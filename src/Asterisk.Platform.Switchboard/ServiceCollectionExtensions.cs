using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Switchboard;

/// <summary>
/// DI registration extensions for Platform.Switchboard services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the conversation switchboard.
    /// </summary>
    public static IServiceCollection AddSwitchboard(this IServiceCollection services)
    {
        services.AddSingleton<IConversationSwitchboard, ConversationSwitchboard>();
        return services;
    }
}
