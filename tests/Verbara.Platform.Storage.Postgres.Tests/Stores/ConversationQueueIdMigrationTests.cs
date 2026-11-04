using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Conversations;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// contact-history-opens-by-assignment task 1.4 (design D7): migration <c>027</c> against a real Postgres built from the
/// real migrations. It adds <c>conversations.queue_id</c> nullable, fills it from the owner where a queue owns the row
/// (<c>owner_kind = 3</c>), leaves every other row NULL (no source exists for them), and a second run changes nothing.
/// Live-DB (Testcontainers): runs in CI's "Live-DB Tests (Postgres)" job.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ConversationQueueIdMigrationTests : IClassFixture<ConversationClockFixture>
{
    public const string Migration027 = "027_ConversationQueueId.sql";

    private const string Tenant = "t-queue-id";
    private const int Queued = (int)ConversationState.Queued;
    private const int Active = (int)ConversationState.Active;
    private const int Closed = (int)ConversationState.Closed;
    private const int AgentKind = (int)ConversationOwnerKind.Agent;
    private const int QueueKind = (int)ConversationOwnerKind.Queue;

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly ConversationClockFixture _fixture;

    public ConversationQueueIdMigrationTests(ConversationClockFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration027_ShouldBackfillTheQueueOnlyWhereAQueueOwnsTheRow_WhenApplied()
    {
        var ds = await _fixture.CreateDatabaseBeforeAsync(Migration027);
        await InsertAsync(ds, "c-queued", Queued, QueueKind, "queue-1");
        await InsertAsync(ds, "c-agent", Active, AgentKind, "agent-1");
        await InsertAsync(ds, "c-closed", Closed, AgentKind, "agent-2");
        await InsertAsync(ds, "c-none", Queued, ownerKind: null, ownerId: null);

        await ConversationClockFixture.ApplyMigrationAsync(ds, Migration027);

        (await QueueIdAsync(ds, "c-queued")).Should().Be("queue-1");
        (await QueueIdAsync(ds, "c-agent")).Should().BeNull("an agent-owned row has no recorded queue to backfill from");
        (await QueueIdAsync(ds, "c-closed")).Should().BeNull();
        (await QueueIdAsync(ds, "c-none")).Should().BeNull();
        (await ConversationClockFixture.ScalarAsync(ds,
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = 'conversations' AND column_name = 'queue_id'"))
            .Should().Be("YES", "the column is added nullable");
    }

    [Fact]
    public async Task Migration027_ShouldChangeNothing_WhenReapplied()
    {
        var ds = await _fixture.CreateDatabaseBeforeAsync(Migration027);
        await InsertAsync(ds, "c-queued", Queued, QueueKind, "queue-1");
        await InsertAsync(ds, "c-agent", Active, AgentKind, "agent-1");
        await ConversationClockFixture.ApplyMigrationAsync(ds, Migration027);

        // What the store writes between two runs: the queue moved on, and an agent row learnt its last queue.
        await ConversationClockFixture.ExecuteAsync(ds,
            "UPDATE conversations SET owner_kind = @Queue, owner_id = 'queue-2', queue_id = 'queue-2' WHERE conversation_id = 'c-queued'",
            new NpgsqlParameter("Queue", NpgsqlDbType.Integer) { Value = QueueKind });
        await ConversationClockFixture.ExecuteAsync(ds,
            "UPDATE conversations SET queue_id = 'queue-9' WHERE conversation_id = 'c-agent'");

        var again = () => ConversationClockFixture.ApplyMigrationAsync(ds, Migration027);

        await again.Should().NotThrowAsync("every step is IF NOT EXISTS or touches only rows still NULL");
        (await QueueIdAsync(ds, "c-queued")).Should().Be("queue-2");
        (await QueueIdAsync(ds, "c-agent")).Should().Be("queue-9", "a re-run never overwrites a stored queue");
    }

    private static async Task InsertAsync(NpgsqlDataSource ds, string id, int state, int? ownerKind, string? ownerId)
    {
        await using var cmd = ds.CreateCommand(
            "INSERT INTO conversations (conversation_id, tenant_id, contact_id, channel, state, owner_kind, owner_id, metadata, created_at) " +
            "VALUES (@Id, @TenantId, 'contact-1', 1, @State, @OwnerKind, @OwnerId, '{}'::jsonb, @CreatedAt)");
        cmd.Parameters.Add(new NpgsqlParameter("Id", id));
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", Tenant));
        cmd.Parameters.Add(new NpgsqlParameter("State", NpgsqlDbType.Integer) { Value = state });
        cmd.Parameters.Add(new NpgsqlParameter("OwnerKind", NpgsqlDbType.Integer) { Value = (object?)ownerKind ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("OwnerId", NpgsqlDbType.Text) { Value = (object?)ownerId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("CreatedAt", NpgsqlDbType.TimestampTz) { Value = Now });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> QueueIdAsync(NpgsqlDataSource ds, string id) =>
        await ConversationClockFixture.ScalarAsync(ds,
            "SELECT queue_id FROM conversations WHERE tenant_id = @T AND conversation_id = @Id",
            new NpgsqlParameter("T", Tenant), new NpgsqlParameter("Id", id)) as string;
}
