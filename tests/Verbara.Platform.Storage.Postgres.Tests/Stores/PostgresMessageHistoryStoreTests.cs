using Verbara.Platform.Conversations.Stores;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// <see cref="MessageHistoryStoreContract"/> against <see cref="PostgresMessageStore"/> on a real Postgres (a fresh
/// database per test, every migration applied): the keyset read of design D10 (contact-history-opens-by-assignment
/// task 2.4).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresMessageHistoryStoreTests : MessageHistoryStoreContract, IClassFixture<ConversationClockFixture>
{
    private readonly ConversationClockFixture _fixture;

    public PostgresMessageHistoryStoreTests(ConversationClockFixture fixture) => _fixture = fixture;

    protected override async Task<IMessageStore> NewStoreAsync() => new PostgresMessageStore(await _fixture.CreateDatabaseAsync());
}
