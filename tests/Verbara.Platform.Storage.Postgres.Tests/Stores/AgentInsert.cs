using Npgsql;
using Verbara.Platform.Queues;
using Verbara.Platform.Storage.Postgres.Stores;

namespace Verbara.Platform.Storage.Postgres.Tests.Stores;

/// <summary>
/// Creates an agent row the way the licensed-agent writer does — <see cref="PostgresAgentStore.InsertAsync"/>
/// in a transaction — without a ledger row, for store tests that only need the agent to exist
/// (licensed-agent-metering: <see cref="PostgresAgentStore.SaveAsync"/> no longer creates one).
/// </summary>
internal static class AgentInsert
{
    public static async Task InsertAsync(NpgsqlDataSource ds, Agent agent)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await PostgresAgentStore.InsertAsync(conn, tx, agent, CancellationToken.None);
        await tx.CommitAsync();
    }
}
