using Microsoft.Extensions.DependencyInjection;
using Asterisk.Platform.Audit;
using Asterisk.Platform.Automation;
using Asterisk.Platform.Bot;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Identity;
using Asterisk.Platform.KnowledgeBase;
using Asterisk.Platform.Media;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Surveys;

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

        // Identity
        services.AddSingleton<IUserStore, InMemoryUserStore>();
        services.AddSingleton<IApiKeyStore, InMemoryApiKeyStore>();
        services.AddSingleton<IServiceAccountStore, InMemoryServiceAccountStore>();

        // Queues
        services.AddSingleton<IQueueStore, InMemoryQueueStore>();
        services.AddSingleton<IAgentStore, InMemoryAgentStore>();
        services.AddSingleton<ITeamStore, InMemoryTeamStore>();

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

        // Media
        services.AddSingleton<IMediaStore, InMemoryMediaStore>();

        return services;
    }
}
