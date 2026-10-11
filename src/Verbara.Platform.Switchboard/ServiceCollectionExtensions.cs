using Verbara.Platform.Conversations.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Switchboard;

/// <summary>
/// DI registration extensions for Platform.Switchboard services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the conversation switchboard, the conversation service, the WhatsApp session window and the
    /// agent-started conversation starter (design D14).
    /// </summary>
    public static IServiceCollection AddSwitchboard(this IServiceCollection services)
    {
        services.AddSingleton<IConversationSwitchboard, ConversationSwitchboard>();
        services.AddSingleton<IConversationService, DefaultConversationService>();
        services.AddSingleton<IWhatsAppSessionWindow, WhatsAppSessionWindow>();
        services.AddSingleton<IOutboundConversationStarter, OutboundConversationStarter>();
        return services;
    }
}
