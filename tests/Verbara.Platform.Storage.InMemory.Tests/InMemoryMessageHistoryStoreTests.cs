using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.InMemory.Tests;

/// <summary><see cref="MessageHistoryStoreContract"/> against <see cref="InMemoryMessageStore"/>.</summary>
public sealed class InMemoryMessageHistoryStoreTests : MessageHistoryStoreContract
{
    protected override Task<IMessageStore> NewStoreAsync() => Task.FromResult<IMessageStore>(new InMemoryMessageStore());
}
