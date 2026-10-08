using System.Data;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Licensing;

/// <summary>
/// The Postgres read side of the licensed-agent reports (licensed-agent-reporting, design D11). Each report is
/// read in one read-only <c>REPEATABLE READ</c> transaction, so the rows and the chain heads come from one
/// snapshot: a chain exported up to now ends exactly at its head even while other replicas append.
/// </summary>
internal sealed class PostgresLicensedAgentReportReader : ILicensedAgentReportReader
{
    // Every row of every chain with the instant it was written and, for a daily row, its day: the two
    // measurements of LicensedAgentExportSpan are taken over it.
    private const string ChainRows =
        "SELECT chain_key, sequence, occurred_at AS at, NULL::date AS day FROM license_agent_events " +
        "UNION ALL SELECT chain_key, sequence, closed_at AS at, day FROM license_agent_daily";

    private const string HeadColumns = "chain_key, tenant_id, head_sequence, head_hash, license_id";

    // Tenant chains by tenant id (byte order, the ordinal order of LicensedAgentExportSpan.CompareChains), then
    // the deployment chain.
    private const string ChainOrder = "(tenant_id IS NULL), chain_key COLLATE \"C\"";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresLicensedAgentReportReader(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<LicensedAgentPeaksData> ReadPeaksAsync(
        DateOnly firstDay, DateOnly lastDay, string fallbackDayZone, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDayZone);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await BeginSnapshotAsync(conn, ct).ConfigureAwait(false);

        var zone = await AnchoredDayZoneAsync(conn, tx, ct).ConfigureAwait(false) ?? fallbackDayZone;

        // The latest revision of each chain's day; a reader only ever reports the highest revision.
        var daily = new List<LicenseAgentDaily>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT DISTINCT ON (t.chain_key, t.day) " + DailyColumns + " FROM license_agent_daily t " +
            "WHERE t.day BETWEEN @From AND @To ORDER BY t.chain_key, t.day, t.revision DESC", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("From", NpgsqlDbType.Date) { Value = firstDay });
            cmd.Parameters.Add(new NpgsqlParameter("To", NpgsqlDbType.Date) { Value = lastDay });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                daily.Add(LicenseAgentRows.MapDaily(reader));
        }

        var tenantIds = daily.Select(d => d.TenantId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (tenantIds.Length > 0)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT tenant_id, name FROM tenants WHERE tenant_id = ANY(@Ids)", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter("Ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = tenantIds });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                names[reader.GetString("tenant_id")] = reader.GetString("name");
        }

        var heads = await HeadsAsync(conn, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new LicensedAgentPeaksData(zone, daily, names, heads);
    }

    public async Task<LicensedAgentExportData> ReadExportAsync(
        DateOnly firstDay, DateOnly lastDay, string fallbackDayZone, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackDayZone);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await BeginSnapshotAsync(conn, ct).ConfigureAwait(false);

        var zoneId = await AnchoredDayZoneAsync(conn, tx, ct).ConfigureAwait(false) ?? fallbackDayZone;
        var (start, end) = LicensedAgentExportSpan.Bounds(firstDay, lastDay, LicenseAgentDayZone.Resolve(zoneId));
        var heads = await HeadsAsync(conn, tx, ct).ConfigureAwait(false);

        // Per chain, the two measurements of the span rule; the arithmetic is LicensedAgentExportSpan.Range.
        var spans = new Dictionary<string, (long First, long Last)>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(
            "SELECT h.chain_key, h.head_sequence, " +
            "MIN(r.sequence) FILTER (WHERE r.at >= @Start) AS first_since, " +
            "MAX(r.sequence) FILTER (WHERE r.at < @End OR r.day BETWEEN @From AND @To) AS last_before " +
            "FROM license_agent_chain_heads h LEFT JOIN (" + ChainRows + ") r ON r.chain_key = h.chain_key " +
            "GROUP BY h.chain_key, h.head_sequence", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("Start", NpgsqlDbType.TimestampTz) { Value = start });
            cmd.Parameters.Add(new NpgsqlParameter("End", NpgsqlDbType.TimestampTz) { Value = end });
            cmd.Parameters.Add(new NpgsqlParameter("From", NpgsqlDbType.Date) { Value = firstDay });
            cmd.Parameters.Add(new NpgsqlParameter("To", NpgsqlDbType.Date) { Value = lastDay });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (LicensedAgentExportSpan.Range(
                        reader.GetInt64OrNull("first_since"), reader.GetInt64OrNull("last_before"),
                        reader.GetInt64("head_sequence")) is { } span)
                {
                    spans[reader.GetString("chain_key")] = span;
                }
            }
        }

        var events = new List<LicenseAgentEvent>();
        var daily = new List<LicenseAgentDaily>();
        if (spans.Count > 0)
        {
            var keys = spans.Keys.ToArray();
            var firsts = keys.Select(k => spans[k].First).ToArray();
            var lasts = keys.Select(k => spans[k].Last).ToArray();

            await ReadSpanAsync(conn, tx, "SELECT " + EventColumns + " FROM license_agent_events t", keys, firsts, lasts,
                r => events.Add(LicenseAgentRows.MapEvent(r)), ct).ConfigureAwait(false);
            await ReadSpanAsync(conn, tx, "SELECT " + DailyColumns + " FROM license_agent_daily t", keys, firsts, lasts,
                r => daily.Add(LicenseAgentRows.MapDaily(r)), ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new LicensedAgentExportData(zoneId, events, daily, heads);
    }

    private const string EventColumns =
        "t.chain_key, t.sequence, t.tenant_id, t.event_id, t.kind, t.occurred_at, t.agent_id, t.user_id, t.actor_user_id, " +
        "t.conversation_id, t.user_status, t.counted, t.license_id, t.prev_hash, t.row_hash";

    private const string DailyColumns =
        "t.chain_key, t.sequence, t.tenant_id, t.day, t.revision, t.licensed_agents, t.closed_at, t.closed_through_sequence, " +
        "t.license_id, t.prev_hash, t.row_hash";

    private static async Task ReadSpanAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string select, string[] keys, long[] firsts, long[] lasts,
        Action<NpgsqlDataReader> map, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            select + " JOIN unnest(@Keys, @Firsts, @Lasts) AS s(chain_key, first_seq, last_seq) " +
            "ON s.chain_key = t.chain_key AND t.sequence BETWEEN s.first_seq AND s.last_seq " +
            "ORDER BY (t.tenant_id IS NULL), t.chain_key COLLATE \"C\", t.sequence", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("Keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = keys });
        cmd.Parameters.Add(new NpgsqlParameter("Firsts", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = firsts });
        cmd.Parameters.Add(new NpgsqlParameter("Lasts", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = lasts });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            map(reader);
    }

    private static async Task<NpgsqlTransaction> BeginSnapshotAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var tx = await conn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return tx;
    }

    private static async Task<string?> AnchoredDayZoneAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT day_zone FROM license_agent_chain_heads WHERE chain_key = @ChainKey", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = LicenseAgentChain.DeploymentChainKey });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string { Length: > 0 } zone ? zone : null;
    }

    private static async Task<List<LicenseAgentChainHead>> HeadsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var heads = new List<LicenseAgentChainHead>();
        await using var cmd = new NpgsqlCommand(
            "SELECT " + HeadColumns + " FROM license_agent_chain_heads ORDER BY " + ChainOrder, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            heads.Add(new LicenseAgentChainHead(
                reader.GetStringOrNull("tenant_id"),
                reader.GetInt64("head_sequence"),
                reader.GetString("head_hash"),
                reader.GetStringOrNull("license_id")));
        }

        return heads;
    }
}
