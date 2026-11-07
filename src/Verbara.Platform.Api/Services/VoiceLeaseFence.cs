using Npgsql;
using NpgsqlTypes;
using Verbara.Sdk.Pro.EventStore;
using Verbara.Sdk.Pro.EventStore.Postgres;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The event store's fence over the voice leader lease (call-records-are-written-once design D13): Pro runs it as the
/// first statement of every fenced append and projection write, inside that write's own transaction, on the event
/// store's database, which the startup probe has proved is the one holding the Sdk's <c>cluster_distributed_lock</c>
/// (D13a). Only <see cref="FenceVerdict.Held"/> lets the write continue; every verdict feeds the writer's continuity chain
/// and its health (D13b, D15).
/// </summary>
/// <remarks>
/// Two statements, the first one first. (1) locks the lease row <b>only if it is this replica's and unexpired by the
/// statement clock</b>, with <c>FOR NO KEY UPDATE</c>, the lock the Sdk's renewal (<c>INSERT … ON CONFLICT DO UPDATE</c>)
/// conflicts with, so a takeover waits for the in-flight write and the next one is refused. (2) runs only when (1)
/// returned no row and classifies the miss with an ordinary read that takes no lock: a refusing replica never queues
/// on another owner's row ahead of that owner's renewal. A renewal that commits between (1) and (2) does not revive the
/// refusal: (2) classifies what (1) saw. (2) is a left join, so it answers <c>statement_timestamp()</c> even when the
/// resource has no row.
/// </remarks>
internal static class VoiceLeaseFence
{
    private const string HeldSql = """
        SELECT expires_at, statement_timestamp() AS observed_at
          FROM cluster_distributed_lock
         WHERE resource = @resource AND owner = @self AND expires_at > statement_timestamp()
           FOR NO KEY UPDATE
        """;

    private const string ClassifySql = """
        SELECT lease.owner, lease.expires_at, statement_timestamp() AS observed_at
          FROM (SELECT 1) AS one
          LEFT JOIN cluster_distributed_lock AS lease ON lease.resource = @resource
        """;

    /// <summary>The fence for <paramref name="resource"/>, held by this replica's lease owner id <paramref name="self"/>.</summary>
    /// <param name="resource">The lease resource, e.g. <see cref="VoiceLeaderResources.AmiOwner"/>.</param>
    /// <param name="self">
    /// This replica's lease owner token: the <c>ClusterLeadershipOptions.InstanceId</c> its leader election writes.
    /// </param>
    public static EventStoreFence Create(string resource, string self)
    {
        ArgumentException.ThrowIfNullOrEmpty(resource);
        ArgumentException.ThrowIfNullOrEmpty(self);
        return (connection, transaction, cancellationToken) =>
            ObserveAsync(connection, transaction, resource, self, cancellationToken);
    }

    private static async ValueTask<FenceObservation> ObserveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string resource,
        string self,
        CancellationToken cancellationToken)
    {
        await using (var held = new NpgsqlCommand(HeldSql, connection, transaction))
        {
            held.Parameters.Add(new NpgsqlParameter("resource", NpgsqlDbType.Text) { Value = resource });
            held.Parameters.Add(new NpgsqlParameter("self", NpgsqlDbType.Text) { Value = self });
            await using var reader = await held.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new FenceObservation(
                    FenceVerdict.Held,
                    reader.GetFieldValue<DateTimeOffset>(0),
                    reader.GetFieldValue<DateTimeOffset>(1));
            }
        }

        await using var classify = new NpgsqlCommand(ClassifySql, connection, transaction);
        classify.Parameters.Add(new NpgsqlParameter("resource", NpgsqlDbType.Text) { Value = resource });
        await using var row = await classify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await row.ReadAsync(cancellationToken).ConfigureAwait(false);
        var observedAt = row.GetFieldValue<DateTimeOffset>(2);
        if (row.IsDBNull(0))
            return new FenceObservation(FenceVerdict.NoRow, null, observedAt);

        var owner = row.GetString(0);
        var expiresAt = row.GetFieldValue<DateTimeOffset>(1);
        var verdict = string.Equals(owner, self, StringComparison.Ordinal) ? FenceVerdict.Expired : FenceVerdict.NotOwner;
        return new FenceObservation(verdict, expiresAt, observedAt);
    }
}
