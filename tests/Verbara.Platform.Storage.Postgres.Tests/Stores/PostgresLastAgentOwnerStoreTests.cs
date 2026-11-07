using Verbara.Platform.Conversations;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// <see cref="LastAgentOwnerStoreContract"/> against <see cref="PostgresConversationStore"/> on a real Postgres (a fresh
/// database per test, every migration applied): design D4's query (conversations-return-to-last-agent task 1.3).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresLastAgentOwnerStoreTests : LastAgentOwnerStoreContract, IClassFixture<ConversationClockFixture>
{
    private readonly ConversationClockFixture _fixture;

    public PostgresLastAgentOwnerStoreTests(ConversationClockFixture fixture) => _fixture = fixture;

    protected override async Task<IConversationStore> NewStoreAsync() =>
        new PostgresConversationStore(await _fixture.CreateDatabaseAsync());
}
