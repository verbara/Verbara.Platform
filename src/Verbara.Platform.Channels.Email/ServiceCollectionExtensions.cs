using Verbara.Platform.Conversations.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Verbara.Platform.Channels.Email;

/// <summary>
/// DI registration extensions for Platform.Channels.Email services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Email connector, webhook handler, message transformer, and supporting services.
    /// Requires an <see cref="IMessageStore"/> to be registered separately for thread resolution.
    /// </summary>
    public static IServiceCollection AddEmail(
        this IServiceCollection services,
        Action<EmailOptions> configure)
    {
        services.Configure(configure);

        services.AddSingleton<IEmailParser, SimpleEmailParser>();
        services.AddSingleton<ISmtpClientWrapper, SmtpClientWrapper>();
        services.AddSingleton<IEmailThreadingContext, InMemoryEmailThreadingContext>();
        services.AddSingleton<EmailThreadResolver>();
        services.AddSingleton<EmailConnector>();
        services.AddSingleton<EmailWebhookHandler>();
        services.AddSingleton<EmailMessageTransformer>();

        return services;
    }
}
