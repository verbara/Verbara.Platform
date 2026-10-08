using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Licensing;

/// <summary>A chain's head, locked for the rest of the caller's transaction.</summary>
internal sealed class LicenseAgentChainCursor
{
    public required string ChainKey { get; init; }
    public required string? TenantId { get; init; }
    public long HeadSequence { get; set; }
    public string HeadHash { get; set; } = "";
    public string? LicenseId { get; set; }
    public string? DayZone { get; init; }
    public DateTimeOffset AnchoredAt { get; init; }

    /// <summary>Whether this transaction anchored the chain (its first rows were written here).</summary>
    public bool AnchoredNow { get; init; }
}

/// <summary>What an agent-bearing ledger row records; the ledger fills in the chain fields.</summary>
internal sealed record LicenseAgentChange(
    string Kind,
    string? AgentId,
    string? UserId,
    string? ActorUserId,
    string? ConversationId,
    string? UserStatus,
    bool? Counted);

/// <summary>
/// The append-only licensed-agent ledger in Postgres (licensed-agent-ledger, design D5/D6). Every method runs
/// inside the caller's transaction, so a ledger row commits with the change it records or not at all.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="LockAsync"/> locks the chain head (<c>SELECT … FOR UPDATE</c>), which serialises the
/// chain's appends: <c>sequence</c> is gap-free and the chain never forks. A missing head anchors the chain:
/// a <c>chain_anchored</c> row linked to the genesis, then, for a tenant chain, one <c>agent_baseline</c> row
/// per agent row that exists. Of two transactions racing to anchor, one inserts the head and the other waits
/// for it and finds it.</item>
/// <item>The loaded licence id is read once per lock. When it differs from the head's, a
/// <c>chain_reanchored</c> row carrying the new id is appended first, linked to the old head.</item>
/// <item>A writer locks the head before its domain write, so the baseline of a chain it anchors reflects the
/// state before the change, and the counted state it reads is not moved by a concurrent writer of the same
/// tenant.</item>
/// </list>
/// </remarks>
internal sealed class LicenseAgentLedger
{
    private readonly ILicenseIdSource _licenses;
    private readonly IClock _clock;

    public LicenseAgentLedger(ILicenseIdSource licenses, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(licenses);
        ArgumentNullException.ThrowIfNull(clock);
        _licenses = licenses;
        _clock = clock;
    }

    private string? CurrentLicenseId => _licenses.CurrentLicenseId is { Length: > 0 } id ? id : null;

    /// <summary>
    /// Locks <paramref name="tenantId"/>'s chain (the deployment chain when null) for the rest of
    /// <paramref name="tx"/>, anchoring it if it has no head and re-anchoring it if the loaded licence changed.
    /// <paramref name="dayZone"/> is recorded only when this call anchors the deployment chain.
    /// </summary>
    public async Task<LicenseAgentChainCursor> LockAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string? tenantId, CancellationToken ct, string? dayZone = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(tx);

        var chainKey = LicenseAgentChain.ChainKeyOf(tenantId);
        var licenseId = CurrentLicenseId;
        var now = LicenseAgentChain.Normalize(_clock.UtcNow);
        var zone = tenantId is null ? dayZone : null;

        LicenseAgentChainCursor cursor;
        if (await TryInsertHeadAsync(conn, tx, chainKey, tenantId, licenseId, zone, now, ct).ConfigureAwait(false))
        {
            cursor = new LicenseAgentChainCursor
            {
                ChainKey = chainKey,
                TenantId = tenantId,
                HeadSequence = 0,
                HeadHash = LicenseAgentChain.Genesis(tenantId, licenseId),
                LicenseId = licenseId,
                DayZone = zone,
                AnchoredAt = now,
                AnchoredNow = true,
            };
            await AppendEventAsync(conn, tx, cursor,
                new LicenseAgentChange(LicenseAgentEventKinds.ChainAnchored, null, null, null, null, null, null), ct)
                .ConfigureAwait(false);
            if (tenantId is not null)
                await AppendBaselineAsync(conn, tx, cursor, ct).ConfigureAwait(false);
            return cursor;
        }

        cursor = await ReadHeadForUpdateAsync(conn, tx, chainKey, tenantId, ct).ConfigureAwait(false);
        if (LicenseAgentChain.NeedsReanchor(cursor.LicenseId, licenseId))
        {
            cursor.LicenseId = licenseId;
            await AppendEventAsync(conn, tx, cursor,
                new LicenseAgentChange(LicenseAgentEventKinds.ChainReanchored, null, null, null, null, null, null), ct)
                .ConfigureAwait(false);
        }

        return cursor;
    }

    /// <summary>Appends one ledger row to the locked chain and moves its head.</summary>
    public async Task<LicenseAgentEvent> AppendEventAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, LicenseAgentChainCursor cursor, LicenseAgentChange change,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(change);

        var row = LicenseAgentChain.Seal(new LicenseAgentEvent
        {
            TenantId = cursor.TenantId,
            Sequence = cursor.HeadSequence + 1,
            EventId = Guid.NewGuid(),
            Kind = change.Kind,
            OccurredAt = LicenseAgentChain.Normalize(_clock.UtcNow),
            AgentId = change.AgentId,
            UserId = change.UserId,
            ActorUserId = change.ActorUserId,
            ConversationId = change.ConversationId,
            UserStatus = change.UserStatus,
            Counted = change.Counted,
            LicenseId = cursor.LicenseId,
            PrevHash = cursor.HeadHash,
            RowHash = "",
        });

        await conn.ExecuteAsync(
            "INSERT INTO license_agent_events (chain_key, sequence, tenant_id, event_id, kind, occurred_at, agent_id, " +
            "user_id, actor_user_id, conversation_id, user_status, counted, license_id, prev_hash, row_hash) " +
            "VALUES (@ChainKey, @Sequence, @TenantId, @EventId, @Kind, @OccurredAt, @AgentId, @UserId, @ActorUserId, " +
            "@ConversationId, @UserStatus, @Counted, @LicenseId, @PrevHash, @RowHash)",
            p =>
            {
                p.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = cursor.ChainKey });
                p.Add(new NpgsqlParameter("Sequence", NpgsqlDbType.Bigint) { Value = row.Sequence });
                p.Add(Text("TenantId", row.TenantId));
                p.Add(new NpgsqlParameter("EventId", NpgsqlDbType.Uuid) { Value = row.EventId });
                p.Add(new NpgsqlParameter("Kind", NpgsqlDbType.Text) { Value = row.Kind });
                p.Add(new NpgsqlParameter("OccurredAt", NpgsqlDbType.TimestampTz) { Value = row.OccurredAt });
                p.Add(Text("AgentId", row.AgentId));
                p.Add(Text("UserId", row.UserId));
                p.Add(Text("ActorUserId", row.ActorUserId));
                p.Add(Text("ConversationId", row.ConversationId));
                p.Add(Text("UserStatus", row.UserStatus));
                p.Add(new NpgsqlParameter("Counted", NpgsqlDbType.Boolean) { Value = (object?)row.Counted ?? DBNull.Value });
                p.Add(Text("LicenseId", row.LicenseId));
                p.Add(new NpgsqlParameter("PrevHash", NpgsqlDbType.Text) { Value = row.PrevHash });
                p.Add(new NpgsqlParameter("RowHash", NpgsqlDbType.Text) { Value = row.RowHash });
            },
            tx, ct).ConfigureAwait(false);

        await MoveHeadAsync(conn, tx, cursor, row.Sequence, row.RowHash, ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>Appends one daily row to the locked chain and moves its head.</summary>
    public async Task<LicenseAgentDaily> AppendDailyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, LicenseAgentChainCursor cursor, DateOnly day, int revision,
        int licensedAgents, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        var row = LicenseAgentChain.Seal(new LicenseAgentDaily
        {
            TenantId = cursor.TenantId,
            Sequence = cursor.HeadSequence + 1,
            Day = day,
            Revision = revision,
            LicensedAgents = licensedAgents,
            ClosedAt = LicenseAgentChain.Normalize(_clock.UtcNow),
            ClosedThroughSequence = cursor.HeadSequence,
            LicenseId = cursor.LicenseId,
            PrevHash = cursor.HeadHash,
            RowHash = "",
        });

        await conn.ExecuteAsync(
            "INSERT INTO license_agent_daily (chain_key, sequence, tenant_id, day, revision, licensed_agents, closed_at, " +
            "closed_through_sequence, license_id, prev_hash, row_hash) " +
            "VALUES (@ChainKey, @Sequence, @TenantId, @Day, @Revision, @LicensedAgents, @ClosedAt, " +
            "@ClosedThroughSequence, @LicenseId, @PrevHash, @RowHash)",
            p =>
            {
                p.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = cursor.ChainKey });
                p.Add(new NpgsqlParameter("Sequence", NpgsqlDbType.Bigint) { Value = row.Sequence });
                p.Add(Text("TenantId", row.TenantId));
                p.Add(new NpgsqlParameter("Day", NpgsqlDbType.Date) { Value = row.Day });
                p.Add(new NpgsqlParameter("Revision", NpgsqlDbType.Integer) { Value = row.Revision });
                p.Add(new NpgsqlParameter("LicensedAgents", NpgsqlDbType.Integer) { Value = row.LicensedAgents });
                p.Add(new NpgsqlParameter("ClosedAt", NpgsqlDbType.TimestampTz) { Value = row.ClosedAt });
                p.Add(new NpgsqlParameter("ClosedThroughSequence", NpgsqlDbType.Bigint) { Value = row.ClosedThroughSequence });
                p.Add(Text("LicenseId", row.LicenseId));
                p.Add(new NpgsqlParameter("PrevHash", NpgsqlDbType.Text) { Value = row.PrevHash });
                p.Add(new NpgsqlParameter("RowHash", NpgsqlDbType.Text) { Value = row.RowHash });
            },
            tx, ct).ConfigureAwait(false);

        await MoveHeadAsync(conn, tx, cursor, row.Sequence, row.RowHash, ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>
    /// Whether <paramref name="agentId"/> counts right now, read inside <paramref name="tx"/>: its agent row
    /// exists in <paramref name="tenantId"/> and its user is <c>Active</c>.
    /// </summary>
    public static async Task<bool> IsCountedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, string agentId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM agents a JOIN users u ON u.tenant_id = a.tenant_id AND u.user_id = a.user_id " +
            "WHERE a.tenant_id = @TenantId AND a.agent_id = @AgentId AND u.status = @Active)", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId });
        cmd.Parameters.Add(new NpgsqlParameter("AgentId", NpgsqlDbType.Text) { Value = agentId });
        cmd.Parameters.Add(new NpgsqlParameter("Active", NpgsqlDbType.Integer) { Value = (int)UserStatus.Active });
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    // One agent_baseline row per agent row of the tenant, in agent-id order, carrying its countability now.
    private async Task AppendBaselineAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, LicenseAgentChainCursor cursor, CancellationToken ct)
    {
        var agents = new List<(string AgentId, string UserId, bool Counted)>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT a.agent_id, a.user_id, COALESCE(u.status = @Active, false) AS counted " +
            "FROM agents a LEFT JOIN users u ON u.tenant_id = a.tenant_id AND u.user_id = a.user_id " +
            "WHERE a.tenant_id = @TenantId ORDER BY a.agent_id", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = cursor.TenantId! });
            cmd.Parameters.Add(new NpgsqlParameter("Active", NpgsqlDbType.Integer) { Value = (int)UserStatus.Active });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                agents.Add((reader.GetString("agent_id"), reader.GetString("user_id"), reader.GetBoolean("counted")));
        }

        foreach (var (agentId, userId, counted) in agents)
        {
            await AppendEventAsync(conn, tx, cursor,
                new LicenseAgentChange(LicenseAgentEventKinds.AgentBaseline, agentId, userId, null, null, null, counted), ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task<bool> TryInsertHeadAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string chainKey, string? tenantId, string? licenseId,
        string? dayZone, DateTimeOffset now, CancellationToken ct)
    {
        // ON CONFLICT DO NOTHING waits for a concurrent anchorer's commit, then returns no row: of two racing
        // transactions exactly one anchors. RETURNING tells this one whether it did.
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO license_agent_chain_heads (chain_key, tenant_id, head_sequence, head_hash, license_id, day_zone, " +
            "anchored_at, updated_at) VALUES (@ChainKey, @TenantId, 0, @Genesis, @LicenseId, @DayZone, @Now, @Now) " +
            "ON CONFLICT (chain_key) DO NOTHING RETURNING chain_key", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = chainKey });
        cmd.Parameters.Add(Text("TenantId", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("Genesis", NpgsqlDbType.Text) { Value = LicenseAgentChain.Genesis(tenantId, licenseId) });
        cmd.Parameters.Add(Text("LicenseId", licenseId));
        cmd.Parameters.Add(Text("DayZone", dayZone));
        cmd.Parameters.Add(new NpgsqlParameter("Now", NpgsqlDbType.TimestampTz) { Value = now });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is string;
    }

    private static async Task<LicenseAgentChainCursor> ReadHeadForUpdateAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string chainKey, string? tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT head_sequence, head_hash, license_id, day_zone, anchored_at FROM license_agent_chain_heads " +
            "WHERE chain_key = @ChainKey FOR UPDATE", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = chainKey });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Licensed-agent chain '{chainKey}' has no head after its anchor insert.");

        return new LicenseAgentChainCursor
        {
            ChainKey = chainKey,
            TenantId = tenantId,
            HeadSequence = reader.GetInt64("head_sequence"),
            HeadHash = reader.GetString("head_hash"),
            LicenseId = reader.GetStringOrNull("license_id"),
            DayZone = reader.GetStringOrNull("day_zone"),
            AnchoredAt = reader.GetDateTimeOffset("anchored_at"),
            AnchoredNow = false,
        };
    }

    private static async Task MoveHeadAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, LicenseAgentChainCursor cursor, long sequence, string hash,
        CancellationToken ct)
    {
        cursor.HeadSequence = sequence;
        cursor.HeadHash = hash;
        // day_zone is never written here: it is set once, by the anchor insert.
        await conn.ExecuteAsync(
            "UPDATE license_agent_chain_heads SET head_sequence = @Sequence, head_hash = @Hash, license_id = @LicenseId, " +
            "updated_at = now() WHERE chain_key = @ChainKey",
            p =>
            {
                p.Add(new NpgsqlParameter("ChainKey", NpgsqlDbType.Text) { Value = cursor.ChainKey });
                p.Add(new NpgsqlParameter("Sequence", NpgsqlDbType.Bigint) { Value = sequence });
                p.Add(new NpgsqlParameter("Hash", NpgsqlDbType.Text) { Value = hash });
                p.Add(Text("LicenseId", cursor.LicenseId));
            },
            tx, ct).ConfigureAwait(false);
    }

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };
}
