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
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Core.Branding;
using Asterisk.Platform.Core.Notifications;
using Asterisk.Platform.Core.Reports;
using Asterisk.Platform.Media;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Queues.Services;
using Asterisk.Platform.Storage.Postgres.Stores;
using Asterisk.Platform.Surveys;
using Asterisk.Platform.Core.Webhooks;
using Asterisk.Sdk.Pro.MultiTenant;

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
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="configureDataSource">
    /// Optional hook for advanced <see cref="NpgsqlDataSourceBuilder"/> configuration —
    /// tracing, name translation, type mappings, etc. AHH Phase 5: pool sizing
    /// is set via connection-string parameters (<c>Maximum Pool Size</c>,
    /// <c>Minimum Pool Size</c>, <c>Connection Idle Lifetime</c>); see the
    /// auth horizontal-scaling runbook for recommended values per knee tier.
    /// </param>
    public static IServiceCollection AddPostgresStorage(
        this IServiceCollection services,
        string connectionString,
        Action<NpgsqlDataSourceBuilder>? configureDataSource = null)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        configureDataSource?.Invoke(dataSourceBuilder);
        services.TryAddSingleton(dataSourceBuilder.Build());

        // Identity — IUserStore + ITenantAuthConfigStore use the AHH Phase 1
        // keyed-decorator pattern: the concrete is registered keyed-as-inner
        // (so the Api project's CachedUserStore / CachedTenantAuthConfigStore
        // decorators can resolve it via IServiceProvider.GetRequiredKeyedService),
        // and the unkeyed alias points at the same instance until the Api
        // bootstrap calls AddAuthHotpathCaching() which replaces it with the
        // decorator. See docs/plans/active/2026-04-27-auth-hotpath-hardening.md
        // Phase 1 + AuthHotpathCacheKeys.
        services.AddKeyedSingleton<IUserStore, PostgresUserStore>(AuthHotpathCacheKeys.UserStoreInner);
        services.AddSingleton<IUserStore>(sp =>
            sp.GetRequiredKeyedService<IUserStore>(AuthHotpathCacheKeys.UserStoreInner));
        services.AddSingleton<IApiKeyStore, PostgresApiKeyStore>();
        services.AddSingleton<IRefreshTokenStore, PostgresRefreshTokenStore>();
        services.AddSingleton<IAuthEventStore, PostgresAuthEventStore>();
        services.AddKeyedSingleton<ITenantAuthConfigStore, PostgresTenantAuthConfigStore>(AuthHotpathCacheKeys.TenantAuthConfigStoreInner);
        services.AddSingleton<ITenantAuthConfigStore>(sp =>
            sp.GetRequiredKeyedService<ITenantAuthConfigStore>(AuthHotpathCacheKeys.TenantAuthConfigStoreInner));

        // Conversations
        services.AddSingleton<IConversationStore, PostgresConversationStore>();
        services.AddSingleton<IMessageStore, PostgresMessageStore>();
        services.AddSingleton<IContactStore, PostgresContactStore>();

        // Queues
        services.AddSingleton<IQueueStore, PostgresQueueStore>();
        services.AddSingleton<IAgentStore, PostgresAgentStore>();
        services.AddSingleton<ITeamStore, PostgresTeamStore>();
        services.AddSingleton<IQueueMembershipStore, PostgresQueueMembershipStore>();
        services.AddSingleton<IAgentCapacityStore, PostgresAgentCapacityStore>();

        // Channels
        services.AddSingleton<ITenantChannelConfigStore, PostgresTenantChannelConfigStore>();

        // Flows
        services.AddSingleton<IFlowStore, PostgresFlowStore>();
        services.AddSingleton<IFlowExecutionStore, PostgresFlowExecutionStore>();

        // Bot
        services.AddSingleton<IBotConfigStore, PostgresBotConfigStore>();
        services.AddSingleton<IBotAnalyticsStore, PostgresBotAnalyticsStore>();

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

        // Billing
        services.AddSingleton<IUsageRecordStore, PostgresUsageRecordStore>();
        services.AddSingleton<ITenantQuotaStore, PostgresTenantQuotaStore>();
        services.AddSingleton<IRateCardStore, PostgresRateCardStore>();
        services.AddSingleton<IInvoiceStore, PostgresInvoiceStore>();
        services.AddSingleton<IPartnerRevenueStore, PostgresPartnerRevenueStore>();

        // GDPR
        services.AddSingleton<IPurgeLogStore, PostgresPurgeLogStore>();
        services.AddSingleton<ITenantRetentionPolicyStore, PostgresTenantRetentionPolicyStore>();

        // Webhooks
        services.AddSingleton<IWebhookSubscriptionStore, PostgresWebhookSubscriptionStore>();
        services.AddSingleton<IWebhookDeliveryStore, PostgresWebhookDeliveryStore>();

        // Reports
        services.AddSingleton<IScheduledReportStore, PostgresScheduledReportStore>();

        // RBAC
        services.AddSingleton<IPermissionStore, PostgresPermissionStore>();
        services.AddSingleton<IRoleTemplateStore, PostgresRoleTemplateStore>();
        services.AddSingleton<ITenantRoleStore, PostgresTenantRoleStore>();
        services.AddSingleton<IUserRoleStore, PostgresUserRoleStore>();

        // MultiTenant
        services.AddSingleton<ITenantStore, PostgresTenantStore>();

        // Branding
        services.AddSingleton<ITenantBrandingStore, PostgresTenantBrandingStore>();

        // Notifications
        services.AddSingleton<INotificationStore, PostgresNotificationStore>();

        // Dunning
        services.AddSingleton<IDunningStore, PostgresDunningStore>();

        // Tenant Add-Ons
        services.AddSingleton<ITenantAddOnStore, PostgresTenantAddOnStore>();

        // Canned Responses
        services.AddSingleton<ICannedResponseStore, PostgresCannedResponseStore>();

        return services;
    }
}
