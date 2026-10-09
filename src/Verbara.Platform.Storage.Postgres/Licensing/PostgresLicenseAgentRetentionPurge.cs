using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Licensing;

/// <summary>
/// The fixed 15-month retention of the licensed-agent ledger (licensed-agent-daily-close, design D8). It is
/// not a tenant retention policy and not a Pro retention target: the window is the constant
/// <see cref="ILicenseAgentRetentionPurge.RetentionMonths"/>, every chain is purged whatever its tenant's
/// policy, and there is no dry run. Rows younger than the window are never deleted by any path.
/// </summary>
/// <remarks>
/// Per chain, in one transaction: the head is locked, the deletion flag is set with <c>SET LOCAL</c> (the
/// 019 triggers refuse any other delete), the purged rows are deleted and a <c>purge_log</c> row records
/// them. The purge removes a prefix of the chain: every row up to the last sequence before the first row
/// still inside the window. A row older than the window that follows a younger one in sequence order (a
/// daily row written just after the cut) waits for the next run. The remaining chain is verified from the
/// tombstone: <c>reason</c> carries the last purged sequence and its <c>rowHash</c>, and
/// <c>entities_deleted</c> the counts and that sequence (the purge-log reader types its values as numbers).
/// </remarks>
internal sealed class PostgresLicenseAgentRetentionPurge : ILicenseAgentRetentionPurge
{
    internal const string SubjectType = "license_agent";
    internal const string PerformedBy = "system:license-agent-retention";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresLicenseAgentRetentionPurge(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<int> PurgeAsync(TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zone);

        // The window, on the day boundary: days before the cut day and rows that occurred before it go.
        var cutDay = LicenseAgentDayZone.DayOf(now, zone).AddMonths(-ILicenseAgentRetentionPurge.RetentionMonths);
        var cutInstant = LicenseAgentDayZone.Bounds(cutDay, zone).Start;

        var chains = await _dataSource.QueryListAsync(
            "SELECT chain_key FROM license_agent_chain_heads ORDER BY chain_key",
            static _ => { },
            static r => r.GetString("chain_key"),
            ct).ConfigureAwait(false);

        var purged = 0;
        foreach (var chainKey in chains)
        {
            var chainPurged = await PurgeChainAsync(chainKey, cutDay, cutInstant, now, ct).ConfigureAwait(false);
            if (chainPurged)
                purged++;
        }

        return purged;
    }

    private async Task<bool> PurgeChainAsync(
        string chainKey, DateOnly cutDay, DateTimeOffset cutInstant, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var lockHead = new NpgsqlCommand(
            "SELECT head_sequence FROM license_agent_chain_heads WHERE chain_key = @ChainKey FOR UPDATE", conn, tx))
        {
            lockHead.Parameters.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = chainKey });
            await lockHead.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        // The last sequence of the purgeable prefix: just before the first row still inside the window.
        long? through;
        await using (var cmd = new NpgsqlCommand(
            "SELECT COALESCE(" +
            "  (SELECT MIN(s) FROM (" +
            "     SELECT MIN(sequence) AS s FROM license_agent_events WHERE chain_key = @ChainKey AND occurred_at >= @CutInstant " +
            "     UNION ALL SELECT MIN(sequence) FROM license_agent_daily WHERE chain_key = @ChainKey AND day >= @CutDay) kept) - 1, " +
            "  (SELECT MAX(s) FROM (" +
            "     SELECT MAX(sequence) AS s FROM license_agent_events WHERE chain_key = @ChainKey " +
            "     UNION ALL SELECT MAX(sequence) FROM license_agent_daily WHERE chain_key = @ChainKey) every))", conn, tx))
        {
            Bind(cmd.Parameters, chainKey, cutDay, cutInstant);
            through = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long s ? s : null;
        }

        if (through is not { } last)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return false;
        }

        var throughHash = await HashAtAsync(conn, tx, chainKey, last, ct).ConfigureAwait(false);
        if (throughHash is null)
        {
            // Nothing at or below the cut (the prefix is empty, or was purged before).
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return false;
        }

        await conn.ExecuteAsync("SET LOCAL verbara.license_purge = 'on'", static _ => { }, tx, ct).ConfigureAwait(false);
        var events = await conn.ExecuteAsync(
            "DELETE FROM license_agent_events WHERE chain_key = @ChainKey AND sequence <= @Through",
            p => BindThrough(p, chainKey, last), tx, ct).ConfigureAwait(false);
        var daily = await conn.ExecuteAsync(
            "DELETE FROM license_agent_daily WHERE chain_key = @ChainKey AND sequence <= @Through",
            p => BindThrough(p, chainKey, last), tx, ct).ConfigureAwait(false);
        await conn.ExecuteAsync("SET LOCAL verbara.license_purge = 'off'", static _ => { }, tx, ct).ConfigureAwait(false);

        await conn.ExecuteAsync(
            "INSERT INTO purge_log (purge_id, tenant_id, subject_type, subject_id, performed_by, reason, entities_deleted, purged_at) " +
            "VALUES (@PurgeId, @TenantId, @SubjectType, @SubjectId, @PerformedBy, @Reason, " +
            "jsonb_build_object('events', @Events, 'daily', @Daily, 'throughSequence', @Through), @PurgedAt)",
            p =>
            {
                p.Add(new NpgsqlParameter("PurgeId", NpgsqlDbType.Varchar) { Value = Guid.NewGuid().ToString("N") });
                p.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Varchar) { Value = chainKey });
                p.Add(new NpgsqlParameter("SubjectType", NpgsqlDbType.Varchar) { Value = SubjectType });
                p.Add(new NpgsqlParameter("SubjectId", NpgsqlDbType.Varchar) { Value = chainKey });
                p.Add(new NpgsqlParameter("PerformedBy", NpgsqlDbType.Varchar) { Value = PerformedBy });
                p.Add(new NpgsqlParameter("Reason", NpgsqlDbType.Varchar)
                {
                    Value = string.Create(CultureInfo.InvariantCulture,
                        $"Fixed {ILicenseAgentRetentionPurge.RetentionMonths}-month licensed-agent retention; through sequence {last} {throughHash}"),
                });
                p.Add(new NpgsqlParameter("Events", NpgsqlDbType.Integer) { Value = events });
                p.Add(new NpgsqlParameter("Daily", NpgsqlDbType.Integer) { Value = daily });
                p.Add(new NpgsqlParameter("Through", NpgsqlDbType.Bigint) { Value = last });
                p.Add(new NpgsqlParameter("PurgedAt", NpgsqlDbType.TimestampTz) { Value = now });
            },
            tx, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<string?> HashAtAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string chainKey, long sequence, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT row_hash FROM license_agent_events WHERE chain_key = @ChainKey AND sequence = @Through " +
            "UNION ALL SELECT row_hash FROM license_agent_daily WHERE chain_key = @ChainKey AND sequence = @Through",
            conn, tx);
        BindThrough(cmd.Parameters, chainKey, sequence);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private static void Bind(NpgsqlParameterCollection p, string chainKey, DateOnly cutDay, DateTimeOffset cutInstant)
    {
        p.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = chainKey });
        p.Add(new NpgsqlParameter("CutDay", NpgsqlDbType.Date) { Value = cutDay });
        p.Add(new NpgsqlParameter("CutInstant", NpgsqlDbType.TimestampTz) { Value = cutInstant });
    }

    private static void BindThrough(NpgsqlParameterCollection p, string chainKey, long through)
    {
        p.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = chainKey });
        p.Add(new NpgsqlParameter("Through", NpgsqlDbType.Bigint) { Value = through });
    }
}
