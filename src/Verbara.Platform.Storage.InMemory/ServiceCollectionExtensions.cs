using Microsoft.Extensions.DependencyInjection;
using Verbara.Platform.Audit;
using Verbara.Platform.Automation;
using Verbara.Platform.Billing;
using Verbara.Platform.Core;
using Verbara.Platform.Bot;
using Verbara.Platform.Channels.Core;
using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Flows;
using Verbara.Platform.Identity;
using Verbara.Platform.KnowledgeBase;
using Verbara.Platform.Media;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Platform.Routing.Inbound;
using Verbara.Platform.Surveys;
using Verbara.Platform.Typification.Stores;
using Verbara.Platform.Core.Branding;
using Verbara.Platform.Core.Notifications;
using Verbara.Platform.Core.Reports;
using Verbara.Platform.Core.Webhooks;
using Verbara.Sdk.Pro.MultiTenant;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Pro.Analytics;
using Verbara.Sdk.Pro.CallAnalytics.Store;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// Extension methods to register all in-memory store implementations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers ConcurrentDictionary-backed in-memory implementations for all store interfaces.
    /// All stores are registered as singletons so data persists across requests within a process lifetime.
    /// </summary>
    public static IServiceCollection AddInMemoryStorage(this IServiceCollection services)
    {
        // Conversations
        services.AddSingleton<IConversationStore, InMemoryConversationStore>();
        services.AddSingleton<IMessageStore, InMemoryMessageStore>();
        services.AddSingleton<IContactStore, InMemoryContactStore>();
        services.AddSingleton<ICaseStore, InMemoryCaseStore>();
        services.AddSingleton<IDispositionStore, InMemoryDispositionStore>();
        services.AddSingleton<IWrapUpStore, InMemoryWrapUpStore>();
        services.AddSingleton<ICannedResponseStore, InMemoryCannedResponseStore>();

        // Identity — IUserStore + ITenantAuthConfigStore use the AHH Phase 1
        // keyed-decorator pattern. Mirrors AddPostgresStorage. See
        // docs/plans/active/2026-04-27-auth-hotpath-hardening.md Phase 1.
        services.AddKeyedSingleton<IUserStore, InMemoryUserStore>(AuthHotpathCacheKeys.UserStoreInner);
        services.AddSingleton<IUserStore>(sp =>
            sp.GetRequiredKeyedService<IUserStore>(AuthHotpathCacheKeys.UserStoreInner));
        services.AddSingleton<IApiKeyStore, InMemoryApiKeyStore>();
        services.AddSingleton<IServiceAccountStore, InMemoryServiceAccountStore>();
        services.AddSingleton<IRefreshTokenStore, InMemoryRefreshTokenStore>();
        services.AddSingleton<IAuthEventStore, InMemoryAuthEventStore>();
        services.AddKeyedSingleton<ITenantAuthConfigStore, InMemoryTenantAuthConfigStore>(AuthHotpathCacheKeys.TenantAuthConfigStoreInner);
        services.AddSingleton<ITenantAuthConfigStore>(sp =>
            sp.GetRequiredKeyedService<ITenantAuthConfigStore>(AuthHotpathCacheKeys.TenantAuthConfigStoreInner));
        services.AddKeyedSingleton<ITenantIpAllowlistStore, InMemoryTenantIpAllowlistStore>(AuthHotpathCacheKeys.IpAllowlistStoreInner);
        services.AddSingleton<ITenantIpAllowlistStore>(sp =>
            sp.GetRequiredKeyedService<ITenantIpAllowlistStore>(AuthHotpathCacheKeys.IpAllowlistStoreInner));
        services.AddSingleton<IPermissionStore, InMemoryPermissionStore>();
        services.AddSingleton<IRoleTemplateStore, InMemoryRoleTemplateStore>();
        services.AddSingleton<ITenantRoleStore, InMemoryTenantRoleStore>();
        services.AddSingleton<IUserRoleStore, InMemoryUserRoleStore>();

        // Queues
        services.AddSingleton<IQueueStore, InMemoryQueueStore>();
        services.AddSingleton<IAgentStore, InMemoryAgentStore>();
        services.AddSingleton<ITeamStore, InMemoryTeamStore>();
        services.AddSingleton<IQueueMembershipStore, InMemoryQueueMembershipStore>();
        services.AddSingleton<IAgentCapacityStore, InMemoryAgentCapacityStore>();

        // Routing — inbound DID → queue routes (Phase 2 voice routing)
        services.AddSingleton<IDidRouteStore, InMemoryDidRouteStore>();

        // Channels
        services.AddSingleton<ITenantChannelConfigStore, InMemoryTenantChannelConfigStore>();

        // Flows
        services.AddSingleton<IFlowStore, InMemoryFlowStore>();
        services.AddSingleton<IFlowExecutionStore, InMemoryFlowExecutionStore>();

        // Bot
        services.AddSingleton<IBotConfigStore, InMemoryBotConfigStore>();
        services.AddSingleton<IBotAnalyticsStore, InMemoryBotAnalyticsStore>();

        // KnowledgeBase
        services.AddSingleton<IArticleStore, InMemoryArticleStore>();

        // Surveys
        services.AddSingleton<ISurveyStore, InMemorySurveyStore>();
        services.AddSingleton<ISurveyResponseStore, InMemorySurveyResponseStore>();

        // Typification
        services.AddSingleton<ITypificationSchemaStore, InMemoryTypificationSchemaStore>();
        services.AddSingleton<ISchemaBindingStore, InMemorySchemaBindingStore>();
        services.AddSingleton<ITypificationSubmissionStore, InMemoryTypificationSubmissionStore>();

        // Automation
        services.AddSingleton<ITimerStore, InMemoryTimerStore>();
        services.AddSingleton<IAutomationRuleStore, InMemoryAutomationRuleStore>();
        services.AddSingleton<IAutomationExecutionLogStore, InMemoryAutomationExecutionLogStore>();

        // Audit
        services.AddSingleton<IAuditStore, InMemoryAuditStore>();

        // Billing
        services.AddSingleton<IUsageRecordStore, InMemoryUsageRecordStore>();
        services.AddSingleton<ITenantQuotaStore, InMemoryTenantQuotaStore>();
        services.AddSingleton<IRateCardStore, InMemoryRateCardStore>();
        services.AddSingleton<IInvoiceStore, InMemoryInvoiceStore>();
        services.AddSingleton<ITenantAddOnStore, InMemoryTenantAddOnStore>();
        services.AddSingleton<IDunningStore, InMemoryDunningStore>();
        services.AddSingleton<IPartnerRevenueStore, InMemoryPartnerRevenueStore>();

        // Branding
        services.AddSingleton<ITenantBrandingStore, InMemoryTenantBrandingStore>();

        // Notifications
        services.AddSingleton<INotificationStore, InMemoryNotificationStore>();

        // GDPR
        services.AddSingleton<IPurgeLogStore, InMemoryPurgeLogStore>();
        services.AddSingleton<ITenantRetentionPolicyStore, InMemoryTenantRetentionPolicyStore>();

        // Webhooks
        services.AddSingleton<IWebhookSubscriptionStore, InMemoryWebhookSubscriptionStore>();
        services.AddSingleton<IWebhookDeliveryStore, InMemoryWebhookDeliveryStore>();

        // Reports
        services.AddSingleton<IScheduledReportStore, InMemoryScheduledReportStore>();

        // MultiTenant
        if (!services.Any(d => d.ServiceType == typeof(ITenantStore)))
            services.AddSingleton<ITenantStore, InMemoryTenantStore>();

        // Pro Analytics (fallback when no Postgres analytics configured)
        if (!services.Any(d => d.ServiceType == typeof(ICompletedSessionStore)))
            services.AddSingleton<ICompletedSessionStore, InMemoryCompletedSessionStore>();
        if (!services.Any(d => d.ServiceType == typeof(IIntervalSnapshotStore)))
            services.AddSingleton<IIntervalSnapshotStore, InMemoryIntervalSnapshotStore>();
        if (!services.Any(d => d.ServiceType == typeof(ICallAnalyticsStore)))
            services.AddSingleton<ICallAnalyticsStore, InMemoryCallAnalyticsStore>();

        // Media
        services.AddSingleton<IMediaStore, InMemoryMediaStore>();

        return services;
    }
}
