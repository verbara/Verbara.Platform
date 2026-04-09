using Microsoft.Extensions.DependencyInjection;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Automation;
using Asterisk.Platform.Billing;
using Asterisk.Platform.Core;
using Asterisk.Platform.Bot;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Identity;
using Asterisk.Platform.KnowledgeBase;
using Asterisk.Platform.Media;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Queues.Services;
using Asterisk.Platform.Surveys;
using Asterisk.Platform.Core.Branding;
using Asterisk.Platform.Core.Notifications;
using Asterisk.Platform.Core.Reports;
using Asterisk.Platform.Core.Webhooks;
using Asterisk.Sdk.Pro.MultiTenant;
using Asterisk.Sdk.Pro.EventStore;
using Asterisk.Sdk.Pro.Analytics;
using Asterisk.Sdk.Pro.CallAnalytics.Store;

namespace Asterisk.Platform.Storage.InMemory;

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

        // Identity
        services.AddSingleton<IUserStore, InMemoryUserStore>();
        services.AddSingleton<IApiKeyStore, InMemoryApiKeyStore>();
        services.AddSingleton<IServiceAccountStore, InMemoryServiceAccountStore>();
        services.AddSingleton<IRefreshTokenStore, InMemoryRefreshTokenStore>();
        services.AddSingleton<IAuthEventStore, InMemoryAuthEventStore>();
        services.AddSingleton<ITenantAuthConfigStore, InMemoryTenantAuthConfigStore>();
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

        // Channels
        services.AddSingleton<ITenantChannelConfigStore, InMemoryTenantChannelConfigStore>();

        // Flows
        services.AddSingleton<IFlowStore, InMemoryFlowStore>();
        services.AddSingleton<IFlowExecutionStore, InMemoryFlowExecutionStore>();

        // Bot
        services.AddSingleton<IBotConfigStore, InMemoryBotConfigStore>();

        // KnowledgeBase
        services.AddSingleton<IArticleStore, InMemoryArticleStore>();

        // Surveys
        services.AddSingleton<ISurveyStore, InMemorySurveyStore>();
        services.AddSingleton<ISurveyResponseStore, InMemorySurveyResponseStore>();

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
