using Verbara.Platform.Conversations;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// <see cref="ConversationOwnershipStoreContract"/> against <see cref="PostgresConversationStore"/> on a real Postgres
/// (a fresh database per test, every migration applied): the store-side <c>queue_id</c> rule of migration 027 and the
/// owner-kind filter of <see cref="ConversationQuery.AssignedAgentId"/> (contact-history-opens-by-assignment 1.4, 1.5).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresConversationOwnershipStoreTests : ConversationOwnershipStoreContract, IClassFixture<ConversationClockFixture>
{
    private readonly ConversationClockFixture _fixture;

    public PostgresConversationOwnershipStoreTests(ConversationClockFixture fixture) => _fixture = fixture;

    protected override async Task<IConversationStore> NewStoreAsync() =>
        new PostgresConversationStore(await _fixture.CreateDatabaseAsync());
}
