using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Platform.Storage.TestSupport;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// <see cref="ContactConversationSummaryStoreContract"/> against <see cref="PostgresConversationStore"/> on a real
/// Postgres (a fresh database per test, every migration applied): the one summary query of design D6, its joins on
/// <c>queue_configs</c>, <c>agents</c>, <c>typification_submissions</c> and <c>typification_submission_corrections</c>,
/// and its lateral read of the newest activity message (contact-history-opens-by-assignment task 2.3).
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresContactConversationSummaryStoreTests
    : ContactConversationSummaryStoreContract, IClassFixture<ConversationClockFixture>
{
    private readonly ConversationClockFixture _fixture;

    public PostgresContactConversationSummaryStoreTests(ConversationClockFixture fixture) => _fixture = fixture;

    protected override async Task<World> NewWorldAsync()
    {
        var ds = await _fixture.CreateDatabaseAsync();
        return new World(
            new PostgresConversationStore(ds),
            new PostgresMessageStore(ds),
            new PostgresQueueStore(ds),
            new PostgresAgentStore(ds),
            agent => AgentInsert.InsertAsync(ds, agent),
            new PostgresTypificationSubmissionStore(ds),
            new PostgresTypificationSubmissionCorrectionStore(ds));
    }
}
