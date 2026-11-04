using Verbara.Platform.Conversations;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary><see cref="ConversationOwnershipStoreContract"/> against <see cref="InMemoryConversationStore"/>.</summary>
public sealed class InMemoryConversationOwnershipStoreTests : ConversationOwnershipStoreContract
{
    protected override Task<IConversationStore> NewStoreAsync() =>
        Task.FromResult<IConversationStore>(new InMemoryConversationStore());
}
