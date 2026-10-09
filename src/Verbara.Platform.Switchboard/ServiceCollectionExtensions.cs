using Verbara.Platform.Conversations.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Switchboard;

/// <summary>
/// DI registration extensions for Platform.Switchboard services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the conversation switchboard, the conversation service and the WhatsApp session window.
    /// </summary>
    public static IServiceCollection AddSwitchboard(this IServiceCollection services)
    {
        services.AddSingleton<IConversationSwitchboard, ConversationSwitchboard>();
        services.AddSingleton<IConversationService, DefaultConversationService>();
        services.AddSingleton<IWhatsAppSessionWindow, WhatsAppSessionWindow>();
        return services;
    }
}
