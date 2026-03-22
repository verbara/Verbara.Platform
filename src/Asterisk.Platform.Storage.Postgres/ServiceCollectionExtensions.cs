using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Asterisk.Platform.Automation;
using Asterisk.Platform.Bot;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Storage.Postgres.Stores;

namespace Asterisk.Platform.Storage.Postgres;

/// <summary>
/// Extension methods to register Dapper/Npgsql-backed PostgreSQL store implementations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Dapper/Npgsql implementations for all 13 store interfaces backed by PostgreSQL.
    /// Creates a singleton <see cref="NpgsqlDataSource"/> from the supplied connection string.
    /// </summary>
    public static IServiceCollection AddPostgresStorage(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));

        // Identity
        services.AddSingleton<IUserStore, PostgresUserStore>();
        services.AddSingleton<IApiKeyStore, PostgresApiKeyStore>();

        // Conversations
        services.AddSingleton<IConversationStore, PostgresConversationStore>();
        services.AddSingleton<IMessageStore, PostgresMessageStore>();
        services.AddSingleton<IContactStore, PostgresContactStore>();

        // Queues
        services.AddSingleton<IQueueStore, PostgresQueueStore>();
        services.AddSingleton<IAgentStore, PostgresAgentStore>();

        // Channels
        services.AddSingleton<ITenantChannelConfigStore, PostgresTenantChannelConfigStore>();

        // Flows
        services.AddSingleton<IFlowStore, PostgresFlowStore>();
        services.AddSingleton<IFlowExecutionStore, PostgresFlowExecutionStore>();

        // Bot
        services.AddSingleton<IBotConfigStore, PostgresBotConfigStore>();

        // Automation
        services.AddSingleton<IAutomationRuleStore, PostgresAutomationRuleStore>();
        services.AddSingleton<ITimerStore, PostgresTimerStore>();

        return services;
    }
}
