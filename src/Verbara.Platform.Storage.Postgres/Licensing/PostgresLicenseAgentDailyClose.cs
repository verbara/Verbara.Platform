using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Data.Npgsql;
using Verbara.Sdk.Pro.MultiTenant;

namespace Verbara.Platform.Storage.Postgres.Licensing;

/// <summary>
/// The daily close in Postgres (licensed-agent-daily-close, design D7). Each chain is closed in its own
/// transaction under its head lock; the unique <c>(chain_key, day, revision)</c> index backs the lock.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Customer tenants are enumerated by <c>type</c> in every status and whatever their parent — never
/// from the active-tenant list and never by walking the hierarchy — so a suspended or orphaned customer is
/// closed like any other. Partner and Platform tenants are never closed.</item>
/// <item>A day's peak is <see cref="LicensedAgentPeak.ComputeDay"/> over the chain's retained rows. An agent
/// that exists and counts now but has no retained row at all (its history aged out of the 15-month window,
/// so it has not changed since) is in the start set of every day.</item>
/// <item>A closed day is never rewritten: a row whose <c>sequence</c> is above the day's
/// <c>closedThroughSequence</c> and whose <c>occurredAt</c> falls inside the day appends the next revision.
/// Only the last 35 days are checked.</item>
/// <item>The deployment total of a day is the sum of each Customer chain's latest row for it, written once
/// every Customer chain anchored by then has closed the day; it is corrected when that sum changes.</item>
/// <item>Licence state is never consulted: the close runs under a missing, expired or grace licence.</item>
/// </list>
/// </remarks>
internal sealed partial class PostgresLicenseAgentDailyClose : ILicenseAgentDailyClose
{
    /// <summary>How far back a closed day is re-checked for late rows.</summary>
    internal const int CorrectionWindowDays = 35;

    private readonly NpgsqlDataSource _dataSource;
    private readonly LicenseAgentLedger _ledger;
    private readonly ILogger<PostgresLicenseAgentDailyClose> _logger;

    public PostgresLicenseAgentDailyClose(
        NpgsqlDataSource dataSource, LicenseAgentLedger ledger, ILogger<PostgresLicenseAgentDailyClose> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _ledger = ledger;
        _logger = logger;
    }

    public async Task<int> PrepareAsync(string dayZone, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dayZone);

        // The check comes before anything is written: a mismatch writes no row, not even a re-anchor.
        if (await ReadAnchoredZoneAsync(ct).ConfigureAwait(false) is { } anchored)
            EnsureSameZone(anchored, dayZone);

        var anchoredNow = 0;
        await using (var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            var deployment = await _ledger.LockAsync(conn, tx, null, ct, dayZone).ConfigureAwait(false);
            // A replica that anchored the chain between the read above and this lock wins; recheck under the lock.
            EnsureSameZone(deployment.DayZone, dayZone);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            if (deployment.AnchoredNow)
                anchoredNow++;
        }

        return anchoredNow + await AnchorMissingCustomerChainsAsync(ct).ConfigureAwait(false);
    }

    public async Task<LicenseAgentCloseResult> CloseAsync(TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var anchored = await AnchorMissingCustomerChainsAsync(ct).ConfigureAwait(false);
        var today = LicenseAgentDayZone.DayOf(now, zone);

        int closed = 0, corrections = 0, failed = 0;
        foreach (var tenantId in await CustomerTenantIdsAsync(withoutHeadOnly: false, ct).ConfigureAwait(false))
        {
            try
            {
                var (c, r) = await CloseTenantChainAsync(tenantId, zone, today, ct).ConfigureAwait(false);
                closed += c;
                corrections += r;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One chain's failure does not stop the others; the deployment total waits for this chain's
                // row and the next tick retries.
                failed++;
                LogChainCloseFailed(_logger, ex, tenantId);
            }
        }

        try
        {
            var (dc, dr) = await CloseDeploymentChainAsync(zone, today, ct).ConfigureAwait(false);
            closed += dc;
            corrections += dr;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failed++;
            LogChainCloseFailed(_logger, ex, LicenseAgentChain.DeploymentChainKey);
        }

        return new LicenseAgentCloseResult(anchored, closed, corrections, failed);
    }

    private static void EnsureSameZone(string? anchored, string configured)
    {
        if (!string.Equals(anchored, configured, StringComparison.Ordinal))
            throw new LicenseAgentDayZoneMismatchException(anchored ?? "(none)", configured);
    }

    private async Task<string?> ReadAnchoredZoneAsync(CancellationToken ct)
    {
        var rows = await _dataSource.QueryListAsync(
            "SELECT day_zone FROM license_agent_chain_heads WHERE chain_key = @ChainKey",
            p => p.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = LicenseAgentChain.DeploymentChainKey }),
            static r => r.GetStringOrNull("day_zone") ?? "",
            ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0] is { Length: > 0 } zone ? zone : "(none)";
    }

    private async Task<int> AnchorMissingCustomerChainsAsync(CancellationToken ct)
    {
        var anchored = 0;
        foreach (var tenantId in await CustomerTenantIdsAsync(withoutHeadOnly: true, ct).ConfigureAwait(false))
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            var chain = await _ledger.LockAsync(conn, tx, tenantId, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            if (chain.AnchoredNow)
                anchored++;
        }

        return anchored;
    }

    // Customer tenants by type, in every status, whatever their parent (an orphan whose parent is null or
    // missing included). Never ITenantStore.GetAllActiveAsync, never a hierarchy walk (design D7, Q3b).
    private async Task<IReadOnlyList<string>> CustomerTenantIdsAsync(bool withoutHeadOnly, CancellationToken ct) =>
        await _dataSource.QueryListAsync(
            "SELECT t.tenant_id FROM tenants t WHERE t.type = @Customer" +
            (withoutHeadOnly ? " AND NOT EXISTS (SELECT 1 FROM license_agent_chain_heads h WHERE h.chain_key = t.tenant_id)" : "") +
            " ORDER BY t.tenant_id",
            p => p.Add(new NpgsqlParameter("Customer", NpgsqlDbType.Integer) { Value = (int)TenantType.Customer }),
            static r => r.GetString("tenant_id"),
            ct).ConfigureAwait(false);

    private async Task<(int Closed, int Corrections)> CloseTenantChainAsync(
        string tenantId, TimeZoneInfo zone, DateOnly today, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var chain = await _ledger.LockAsync(conn, tx, tenantId, ct).ConfigureAwait(false);

        var latest = await LatestDailyRowsAsync(conn, tx, chain.ChainKey, ct).ConfigureAwait(false);
        var anchorDay = LicenseAgentDayZone.DayOf(chain.AnchoredAt, zone);
        var firstOpen = latest.Count == 0 ? anchorDay : latest.Keys.Max().AddDays(1);
        var toClose = Days(firstOpen, today);
        var toCorrect = await LateDaysAsync(conn, tx, chain.ChainKey, latest, zone, today, ct).ConfigureAwait(false);

        if (toClose.Count == 0 && toCorrect.Count == 0)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (0, 0);
        }

        var lastDay = toClose.Concat(toCorrect).Max();
        var rows = await EventsBeforeAsync(conn, tx, chain.ChainKey, LicenseAgentDayZone.Bounds(lastDay, zone).End, ct).ConfigureAwait(false);
        var idle = await CountedAgentsWithoutRowsAsync(conn, tx, tenantId, ct).ConfigureAwait(false);

        int Peak(DateOnly day)
        {
            var (start, end) = LicenseAgentDayZone.Bounds(day, zone);
            var startSet = new HashSet<string>(LicensedAgentPeak.StartSet(rows.Where(r => r.OccurredAt < start)), StringComparer.Ordinal);
            startSet.UnionWith(idle);
            return LicensedAgentPeak.Compute(startSet, rows.Where(r => r.OccurredAt >= start && r.OccurredAt < end));
        }

        foreach (var day in toClose)
            await _ledger.AppendDailyAsync(conn, tx, chain, day, 0, Peak(day), ct).ConfigureAwait(false);
        foreach (var day in toCorrect)
            await _ledger.AppendDailyAsync(conn, tx, chain, day, latest[day].Revision + 1, Peak(day), ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (toClose.Count, toCorrect.Count);
    }

    private async Task<(int Closed, int Corrections)> CloseDeploymentChainAsync(
        TimeZoneInfo zone, DateOnly today, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var chain = await _ledger.LockAsync(conn, tx, null, ct).ConfigureAwait(false);

        var latest = await LatestDailyRowsAsync(conn, tx, chain.ChainKey, ct).ConfigureAwait(false);
        var anchorDay = LicenseAgentDayZone.DayOf(chain.AnchoredAt, zone);
        var firstOpen = latest.Count == 0 ? anchorDay : latest.Keys.Max().AddDays(1);
        var customerChains = await CustomerChainAnchorsAsync(conn, tx, ct).ConfigureAwait(false);

        int closed = 0, corrections = 0;
        foreach (var day in Days(firstOpen, today))
        {
            var sum = await DeploymentSumAsync(conn, tx, customerChains, day, zone, ct).ConfigureAwait(false);
            if (sum is null)
                break; // a Customer chain has not closed this day yet: the next tick retries, in day order
            await _ledger.AppendDailyAsync(conn, tx, chain, day, 0, sum.Value, ct).ConfigureAwait(false);
            closed++;
        }

        var windowStart = today.AddDays(-CorrectionWindowDays);
        foreach (var (day, row) in latest.Where(kv => kv.Key >= windowStart).OrderBy(kv => kv.Key))
        {
            var sum = await DeploymentSumAsync(conn, tx, customerChains, day, zone, ct).ConfigureAwait(false);
            if (sum is { } value && value != row.LicensedAgents)
            {
                await _ledger.AppendDailyAsync(conn, tx, chain, day, row.Revision + 1, value, ct).ConfigureAwait(false);
                corrections++;
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return (closed, corrections);
    }

    // The sum of each Customer chain's latest row for the day, or null while a Customer chain anchored before
    // the day ended has no row for it yet.
    private static async Task<int?> DeploymentSumAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, IReadOnlyDictionary<string, DateTimeOffset> customerChains,
        DateOnly day, TimeZoneInfo zone, CancellationToken ct)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var cmd = new NpgsqlCommand(
            "SELECT DISTINCT ON (d.chain_key) d.chain_key, d.licensed_agents FROM license_agent_daily d " +
            "JOIN tenants t ON t.tenant_id = d.chain_key AND t.type = @Customer " +
            "WHERE d.day = @Day ORDER BY d.chain_key, d.revision DESC", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("Customer", NpgsqlDbType.Integer) { Value = (int)TenantType.Customer });
            cmd.Parameters.Add(new NpgsqlParameter("Day", NpgsqlDbType.Date) { Value = day });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                values[reader.GetString("chain_key")] = reader.GetInt32("licensed_agents");
        }

        var dayEnd = LicenseAgentDayZone.Bounds(day, zone).End;
        foreach (var (chainKey, anchoredAt) in customerChains)
        {
            if (anchoredAt < dayEnd && !values.ContainsKey(chainKey))
                return null;
        }

        return values.Values.Sum();
    }

    private static async Task<IReadOnlyDictionary<string, DateTimeOffset>> CustomerChainAnchorsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var anchors = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(
            "SELECT h.chain_key, h.anchored_at FROM license_agent_chain_heads h " +
            "JOIN tenants t ON t.tenant_id = h.chain_key AND t.type = @Customer", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("Customer", NpgsqlDbType.Integer) { Value = (int)TenantType.Customer });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            anchors[reader.GetString("chain_key")] = reader.GetDateTimeOffset("anchored_at");
        return anchors;
    }

    private sealed record LatestDaily(int Revision, int LicensedAgents, long ClosedThroughSequence);

    private static async Task<Dictionary<DateOnly, LatestDaily>> LatestDailyRowsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string chainKey, CancellationToken ct)
    {
        var latest = new Dictionary<DateOnly, LatestDaily>();
        await using var cmd = new NpgsqlCommand(
            "SELECT DISTINCT ON (day) day, revision, licensed_agents, closed_through_sequence FROM license_agent_daily " +
            "WHERE chain_key = @ChainKey ORDER BY day, revision DESC", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = chainKey });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            latest[reader.GetDateOnly("day")] = new LatestDaily(
                reader.GetInt32("revision"), reader.GetInt32("licensed_agents"), reader.GetInt64("closed_through_sequence"));
        }

        return latest;
    }

    // Closed days in the correction window that a row written after their close falls inside.
    private static async Task<List<DateOnly>> LateDaysAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string chainKey, IReadOnlyDictionary<DateOnly, LatestDaily> latest,
        TimeZoneInfo zone, DateOnly today, CancellationToken ct)
    {
        var window = latest.Where(kv => kv.Key >= today.AddDays(-CorrectionWindowDays)).ToList();
        if (window.Count == 0)
            return [];

        var since = window.Min(kv => kv.Value.ClosedThroughSequence);
        var late = new List<(long Sequence, DateTimeOffset OccurredAt)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT sequence, occurred_at FROM license_agent_events WHERE chain_key = @ChainKey AND sequence > @Since " +
            "AND agent_id IS NOT NULL", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = chainKey });
            cmd.Parameters.Add(new NpgsqlParameter("Since", NpgsqlDbType.Bigint) { Value = since });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                late.Add((reader.GetInt64("sequence"), reader.GetDateTimeOffset("occurred_at")));
        }

        return window
            .Where(kv =>
            {
                var (start, end) = LicenseAgentDayZone.Bounds(kv.Key, zone);
                return late.Any(l => l.Sequence > kv.Value.ClosedThroughSequence && l.OccurredAt >= start && l.OccurredAt < end);
            })
            .Select(kv => kv.Key)
            .OrderBy(d => d)
            .ToList();
    }

    private static async Task<List<LicenseAgentEvent>> EventsBeforeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string chainKey, DateTimeOffset before, CancellationToken ct)
    {
        var rows = new List<LicenseAgentEvent>();
        await using var cmd = new NpgsqlCommand(
            "SELECT sequence, tenant_id, event_id, kind, occurred_at, agent_id, user_id, actor_user_id, conversation_id, " +
            "user_status, counted, license_id, prev_hash, row_hash FROM license_agent_events " +
            "WHERE chain_key = @ChainKey AND occurred_at < @Before ORDER BY sequence", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = chainKey });
        cmd.Parameters.Add(new NpgsqlParameter("Before", NpgsqlDbType.TimestampTz) { Value = before });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(LicenseAgentRows.MapEvent(reader));
        return rows;
    }

    // Agents of the tenant that count now and have no retained ledger row: their history aged out of the
    // window, so they have not changed since and counted on every day still being closed.
    private static async Task<IReadOnlyList<string>> CountedAgentsWithoutRowsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, CancellationToken ct)
    {
        var ids = new List<string>();
        await using var cmd = new NpgsqlCommand(
            "SELECT a.agent_id FROM agents a JOIN users u ON u.tenant_id = a.tenant_id AND u.user_id = a.user_id " +
            "WHERE a.tenant_id = @TenantId AND u.status = @Active AND NOT EXISTS (SELECT 1 FROM license_agent_events e " +
            "WHERE e.chain_key = @TenantId AND e.agent_id = a.agent_id)", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId });
        cmd.Parameters.Add(new NpgsqlParameter("Active", NpgsqlDbType.Integer) { Value = (int)UserStatus.Active });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            ids.Add(reader.GetString("agent_id"));
        return ids;
    }

    private static List<DateOnly> Days(DateOnly first, DateOnly todayExclusive)
    {
        var days = new List<DateOnly>();
        for (var day = first; day < todayExclusive; day = day.AddDays(1))
            days.Add(day);
        return days;
    }

    [LoggerMessage(EventId = 7520, Level = LogLevel.Error,
        Message = "The licensed-agent daily close of chain {ChainKey} failed; the next tick retries it.")]
    private static partial void LogChainCloseFailed(ILogger logger, Exception exception, string chainKey);
}

/// <summary>Maps <c>license_agent_events</c> and <c>license_agent_daily</c> rows.</summary>
internal static class LicenseAgentRows
{
    public static LicenseAgentEvent MapEvent(NpgsqlDataReader r) => new()
    {
        TenantId = r.GetStringOrNull("tenant_id"),
        Sequence = r.GetInt64("sequence"),
        EventId = r.GetGuid("event_id"),
        Kind = r.GetString("kind"),
        OccurredAt = r.GetDateTimeOffset("occurred_at"),
        AgentId = r.GetStringOrNull("agent_id"),
        UserId = r.GetStringOrNull("user_id"),
        ActorUserId = r.GetStringOrNull("actor_user_id"),
        ConversationId = r.GetStringOrNull("conversation_id"),
        UserStatus = r.GetStringOrNull("user_status"),
        Counted = r.GetBooleanOrNull("counted"),
        LicenseId = r.GetStringOrNull("license_id"),
        PrevHash = r.GetString("prev_hash"),
        RowHash = r.GetString("row_hash"),
    };

    public static LicenseAgentDaily MapDaily(NpgsqlDataReader r) => new()
    {
        TenantId = r.GetStringOrNull("tenant_id"),
        Sequence = r.GetInt64("sequence"),
        Day = r.GetDateOnly("day"),
        Revision = r.GetInt32("revision"),
        LicensedAgents = r.GetInt32("licensed_agents"),
        ClosedAt = r.GetDateTimeOffset("closed_at"),
        ClosedThroughSequence = r.GetInt64("closed_through_sequence"),
        LicenseId = r.GetStringOrNull("license_id"),
        PrevHash = r.GetString("prev_hash"),
        RowHash = r.GetString("row_hash"),
    };
}
