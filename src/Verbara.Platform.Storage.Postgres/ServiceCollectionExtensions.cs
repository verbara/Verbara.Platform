using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Verbara.Platform.Automation;
using Verbara.Platform.Bot;
using Verbara.Platform.KnowledgeBase;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Flows;
using Verbara.Platform.Identity;
using Verbara.Platform.Llm;
using Verbara.Platform.Audit;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Branding;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Core.Reports;
using Verbara.Platform.Media;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Surveys;
using Verbara.Platform.Typification.Ai;
using Verbara.Platform.Typification.Stores;
using Verbara.Platform.Core.Webhooks;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.Postgres;

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
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        configureDataSource?.Invoke(dataSourceBuilder);
        return services.AddPostgresStorage(dataSourceBuilder.Build());
    }

    /// <summary>
    /// Registers Dapper/Npgsql implementations for all 17 store interfaces backed by
    /// PostgreSQL using a pre-built <see cref="NpgsqlDataSource"/>. Prefer this overload
    /// when the host application owns a shared <see cref="NpgsqlDataSource"/> instance
    /// (ADR-0015 Phase 2): pass the same instance here AND to every Pro
    /// <c>Use*Storage(IServiceCollection, NpgsqlDataSource)</c> call to collapse the
    /// 14-pool sprawl into a single connection pool per host instance.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dataSource">A pre-built <see cref="NpgsqlDataSource"/>; registered as
    /// the canonical DI singleton via <c>TryAddSingleton</c>.</param>
    public static IServiceCollection AddPostgresStorage(
        this IServiceCollection services,
        NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(dataSource);

        services.TryAddSingleton(dataSource);

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
        services.AddKeyedSingleton<ITenantIpAllowlistStore, PostgresTenantIpAllowlistStore>(AuthHotpathCacheKeys.IpAllowlistStoreInner);
        services.AddSingleton<ITenantIpAllowlistStore>(sp =>
            sp.GetRequiredKeyedService<ITenantIpAllowlistStore>(AuthHotpathCacheKeys.IpAllowlistStoreInner));

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

        // Routing — inbound DID → queue routes (Phase 2 voice routing)
        services.AddSingleton<IDidRouteStore, PostgresDidRouteStore>();

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

        // Conversations — cases
        services.AddSingleton<ICaseStore, PostgresCaseStore>();

        // Identity — service accounts
        services.AddSingleton<IServiceAccountStore, PostgresServiceAccountStore>();

        // Media
        services.AddSingleton<IMediaStore, PostgresMediaStore>();

        // Surveys
        services.AddSingleton<ISurveyStore, PostgresSurveyStore>();
        services.AddSingleton<ISurveyResponseStore, PostgresSurveyResponseStore>();

        // Typification
        services.AddSingleton<ITypificationSchemaStore, PostgresTypificationSchemaStore>();
        services.AddSingleton<ISchemaBindingStore, PostgresSchemaBindingStore>();
        services.AddSingleton<ITypificationSubmissionStore, PostgresTypificationSubmissionStore>();
        services.AddSingleton<ITypificationSubmissionCorrectionStore, PostgresTypificationSubmissionCorrectionStore>();
        services.AddSingleton<IReasonHintStore, PostgresReasonHintStore>();
        services.AddSingleton<IAiSuggestionStore, PostgresAiSuggestionStore>();
        services.AddSingleton<ITenantLlmConfigStore, PostgresTenantLlmConfigStore>();
        services.AddSingleton<ITenantAutonomousDispositionStore, PostgresTenantAutonomousDispositionStore>();

        // Audit
        services.AddSingleton<IAuditStore, PostgresAuditStore>();

        // Billing
        services.AddSingleton<IUsageRecordStore, PostgresUsageRecordStore>();
        services.AddSingleton<ITenantQuotaStore, PostgresTenantQuotaStore>();
        services.AddSingleton<IRateCardStore, PostgresRateCardStore>();
        services.AddSingleton<IInvoiceStore, PostgresInvoiceStore>();
        services.AddSingleton<IPartnerRevenueStore, PostgresPartnerRevenueStore>();
        services.AddSingleton<ICreditLedgerStore, PostgresCreditLedgerStore>();

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

    /// <summary>
    /// Registers <see cref="OidcClientSecretEncryptionMigrator"/> as an
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService"/>. Closes
    /// <c>PREPUB-2026-05-09-ADMIN-001</c>: encrypts any legacy plaintext
    /// <c>oidc_client_secret</c> rows in <c>tenant_auth_config</c> at process
    /// startup. Idempotent — safe to register unconditionally; subsequent runs
    /// find every row already encrypted and perform zero writes.
    /// </summary>
    public static IServiceCollection AddOidcClientSecretEncryptionMigrator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<OidcClientSecretEncryptionMigrator>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="TenantLlmConfigSeedMigrator"/> as an
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> (P2c.1, spec §6). Materialises the
    /// appsettings global <c>LlmProviderOptions</c> into the single operational tenant's
    /// <c>tenant_llm_config</c> row — the single-tenant / dev migration off the retired shared
    /// global LLM key. Idempotent + fail-safe (no-op when unconfigured, when there is not exactly
    /// one operational tenant, or when a config row already exists); safe to register unconditionally.
    /// </summary>
    public static IServiceCollection AddTenantLlmConfigSeedMigrator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<TenantLlmConfigSeedMigrator>();
        return services;
    }
}
