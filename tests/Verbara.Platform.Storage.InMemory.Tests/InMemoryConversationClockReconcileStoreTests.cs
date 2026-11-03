using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary><see cref="ConversationClockReconcileStoreContract"/> against <see cref="InMemoryConversationStore"/>.</summary>
public sealed class InMemoryConversationClockReconcileStoreTests : ConversationClockReconcileStoreContract
{
    protected override Task<(IConversationStore Conversations, IMessageStore Messages)> NewStoresAsync()
    {
        var messages = new InMemoryMessageStore();
        return Task.FromResult<(IConversationStore, IMessageStore)>((new InMemoryConversationStore(messages), messages));
    }
}
