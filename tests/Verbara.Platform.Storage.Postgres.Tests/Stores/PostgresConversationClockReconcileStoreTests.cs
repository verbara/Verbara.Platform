using Verbara.Platform.Conversations;
using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// <see cref="ConversationClockReconcileStoreContract"/> against <see cref="PostgresConversationStore"/> and
/// <see cref="PostgresMessageStore"/> on a real Postgres (a fresh database per test, every migration applied; task 2.8).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresConversationClockReconcileStoreTests
    : ConversationClockReconcileStoreContract, IClassFixture<ConversationClockFixture>
{
    private readonly ConversationClockFixture _fixture;

    public PostgresConversationClockReconcileStoreTests(ConversationClockFixture fixture) => _fixture = fixture;

    protected override async Task<(IConversationStore Conversations, IMessageStore Messages)> NewStoresAsync()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        return (new PostgresConversationStore(ds), new PostgresMessageStore(ds));
    }
}
