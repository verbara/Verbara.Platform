using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary>
/// <see cref="ContactConversationSummaryStoreContract"/> against <see cref="InMemoryConversationStore"/>, joined to the
/// in-memory message, queue, agent and typification stores.
/// </summary>
public sealed class InMemoryContactConversationSummaryStoreTests : ContactConversationSummaryStoreContract
{
    protected override Task<World> NewWorldAsync()
    {
        var messages = new InMemoryMessageStore();
        var queues = new InMemoryQueueStore();
        var agents = new InMemoryAgentStore();
        var submissions = new InMemoryTypificationSubmissionStore();
        var corrections = new InMemoryTypificationSubmissionCorrectionStore();
        var conversations = new InMemoryConversationStore(
            messages, new InMemoryContactSummarySources(() => queues, () => agents, () => submissions, () => corrections));
        return Task.FromResult(new World(
            conversations, messages, queues, agents, agent => agents.SaveAsync(agent, CancellationToken.None), submissions, corrections));
    }
}
