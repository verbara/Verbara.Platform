using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Asterisk.Platform.Automation;
using Asterisk.Platform.Bot;
using Asterisk.Platform.KnowledgeBase;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Identity;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Media;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Storage.Postgres.Stores;
using Asterisk.Platform.Surveys;

namespace Asterisk.Platform.Storage.Postgres;

/// <summary>
/// Extension methods to register Dapper/Npgsql-backed PostgreSQL store implementations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Dapper/Npgsql implementations for all 17 store interfaces backed by PostgreSQL.
    /// Creates a singleton <see cref="NpgsqlDataSource"/> from the supplied connection string.
    /// </summary>
    public static IServiceCollection AddPostgresStorage(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(NpgsqlDataSource.Create(connectionString));

        // Identity
        services.AddSingleton<IUserStore, PostgresUserStore>();
        services.AddSingleton<IApiKeyStore, PostgresApiKeyStore>();
        services.AddSingleton<IRefreshTokenStore, PostgresRefreshTokenStore>();
        services.AddSingleton<IAuthEventStore, PostgresAuthEventStore>();
        services.AddSingleton<ITenantAuthConfigStore, PostgresTenantAuthConfigStore>();

        // Conversations
        services.AddSingleton<IConversationStore, PostgresConversationStore>();
        services.AddSingleton<IMessageStore, PostgresMessageStore>();
        services.AddSingleton<IContactStore, PostgresContactStore>();

        // Queues
        services.AddSingleton<IQueueStore, PostgresQueueStore>();
        services.AddSingleton<IAgentStore, PostgresAgentStore>();
        services.AddSingleton<ITeamStore, PostgresTeamStore>();
        services.AddSingleton<IQueueMembershipStore, PostgresQueueMembershipStore>();

        // Channels
        services.AddSingleton<ITenantChannelConfigStore, PostgresTenantChannelConfigStore>();

        // Flows
        services.AddSingleton<IFlowStore, PostgresFlowStore>();
        services.AddSingleton<IFlowExecutionStore, PostgresFlowExecutionStore>();

        // Bot
        services.AddSingleton<IBotConfigStore, PostgresBotConfigStore>();

        // Automation
        services.AddSingleton<IAutomationRuleStore, PostgresAutomationRuleStore>();
        services.AddSingleton<IAutomationExecutionLogStore, PostgresAutomationLogStore>();
        services.AddSingleton<ITimerStore, PostgresTimerStore>();

        // KnowledgeBase
        services.AddSingleton<IArticleStore, PostgresArticleStore>();

        // Conversations — wrap-up + dispositions + cases
        services.AddSingleton<IWrapUpStore, PostgresWrapUpStore>();
        services.AddSingleton<IDispositionStore, PostgresDispositionStore>();
        services.AddSingleton<ICaseStore, PostgresCaseStore>();

        // Identity — service accounts
        services.AddSingleton<IServiceAccountStore, PostgresServiceAccountStore>();

        // Media
        services.AddSingleton<IMediaStore, PostgresMediaStore>();

        // Surveys
        services.AddSingleton<ISurveyStore, PostgresSurveyStore>();
        services.AddSingleton<ISurveyResponseStore, PostgresSurveyResponseStore>();

        // Audit
        services.AddSingleton<IAuditStore, PostgresAuditStore>();

        // RBAC
        services.AddSingleton<IPermissionStore, PostgresPermissionStore>();
        services.AddSingleton<IRoleTemplateStore, PostgresRoleTemplateStore>();
        services.AddSingleton<ITenantRoleStore, PostgresTenantRoleStore>();
        services.AddSingleton<IUserRoleStore, PostgresUserRoleStore>();

        return services;
    }
}
