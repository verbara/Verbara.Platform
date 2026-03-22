using Asterisk.Platform.Automation;
using Asterisk.Platform.Bot;
using Asterisk.Platform.Channels.Core;
using Asterisk.Platform.Conversations;
using Asterisk.Platform.Conversations.Stores;
using Asterisk.Platform.Flows;
using Asterisk.Platform.Identity;
using Asterisk.Platform.KnowledgeBase;
using Asterisk.Platform.Queues;
using Asterisk.Platform.Storage.InMemory;
using Asterisk.Platform.Surveys;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Asterisk.Platform.Storage.InMemory.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddInMemoryStorage_ShouldRegisterAllStoreInterfaces()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        var provider = services.BuildServiceProvider();

        // Conversations
        provider.GetService<IConversationStore>().Should().NotBeNull();
        provider.GetService<IMessageStore>().Should().NotBeNull();
        provider.GetService<IContactStore>().Should().NotBeNull();
        provider.GetService<ICaseStore>().Should().NotBeNull();

        // Identity
        provider.GetService<IUserStore>().Should().NotBeNull();
        provider.GetService<IApiKeyStore>().Should().NotBeNull();
        provider.GetService<IServiceAccountStore>().Should().NotBeNull();

        // Queues
        provider.GetService<IQueueStore>().Should().NotBeNull();
        provider.GetService<IAgentStore>().Should().NotBeNull();
        provider.GetService<ITeamStore>().Should().NotBeNull();

        // Channels
        provider.GetService<ITenantChannelConfigStore>().Should().NotBeNull();

        // Flows
        provider.GetService<IFlowStore>().Should().NotBeNull();
        provider.GetService<IFlowExecutionStore>().Should().NotBeNull();

        // Bot
        provider.GetService<IBotConfigStore>().Should().NotBeNull();

        // KnowledgeBase
        provider.GetService<IArticleStore>().Should().NotBeNull();

        // Surveys
        provider.GetService<ISurveyStore>().Should().NotBeNull();
        provider.GetService<ISurveyResponseStore>().Should().NotBeNull();

        // Automation
        provider.GetService<ITimerStore>().Should().NotBeNull();
        provider.GetService<IAutomationRuleStore>().Should().NotBeNull();
        provider.GetService<IAutomationExecutionLogStore>().Should().NotBeNull();
    }

    [Fact]
    public void AddInMemoryStorage_ShouldRegisterStoresAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        var provider = services.BuildServiceProvider();

        var store1 = provider.GetRequiredService<IConversationStore>();
        var store2 = provider.GetRequiredService<IConversationStore>();

        store1.Should().BeSameAs(store2);
    }
}
