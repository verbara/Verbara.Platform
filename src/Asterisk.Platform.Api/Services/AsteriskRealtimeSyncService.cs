using Dapper;
using Npgsql;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Writes Platform entities (agents, queues, queue members) into the Asterisk
/// Realtime tables (ps_endpoints, ps_auths, ps_aors, rt_queues, rt_queue_members)
/// so that Asterisk picks up configuration changes without a reload.
/// </summary>
/// <remarks>
/// All Asterisk IDs are prefixed with <c>{tenantId}-</c> to provide multi-tenant
/// namespace isolation within a shared Asterisk instance.
/// </remarks>
public sealed class AsteriskRealtimeSyncService
{
    private readonly NpgsqlDataSource _dataSource;

    public AsteriskRealtimeSyncService(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource;

    // -------------------------------------------------------------------------
    // Agent → ps_endpoints + ps_auths + ps_aors
    // -------------------------------------------------------------------------

    /// <summary>
    /// Upserts PJSIP endpoint, auth, and AOR rows for the given agent so that
    /// Asterisk recognises the SIP device without a reload.
    /// </summary>
    public async Task SyncAgentAsync(
        string tenantId,
        string agentId,
        string displayName,
        string? extension,
        string? sipPassword,
        CancellationToken ct = default)
    {
        var sipId  = $"{tenantId}-agent-{agentId}";
        var authId = $"auth-{sipId}";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Upsert ps_endpoints
        await conn.ExecuteAsync("""
            INSERT INTO ps_endpoints (id, transport, aors, auth, context, disallow, allow, callerid, direct_media, force_rport, rewrite_contact, rtp_symmetric, dtmf_mode)
            VALUES (@Id, 'transport-udp', @Id, @AuthId, 'from-internal', 'all', 'opus,g722,ulaw,alaw', @CallerId, 'no', 'yes', 'yes', 'yes', 'rfc4733')
            ON CONFLICT (id) DO UPDATE SET callerid = EXCLUDED.callerid, allow = EXCLUDED.allow
            """,
            new { Id = sipId, AuthId = authId, CallerId = $"{displayName} <{extension}>" });

        // Upsert ps_auths
        await conn.ExecuteAsync("""
            INSERT INTO ps_auths (id, auth_type, username, password)
            VALUES (@Id, 'userpass', @Username, @Password)
            ON CONFLICT (id) DO UPDATE SET username = EXCLUDED.username, password = EXCLUDED.password
            """,
            new { Id = authId, Username = extension, Password = sipPassword });

        // Upsert ps_aors
        await conn.ExecuteAsync("""
            INSERT INTO ps_aors (id, max_contacts, remove_existing, qualify_frequency)
            VALUES (@Id, 1, 'yes', 30)
            ON CONFLICT (id) DO UPDATE SET max_contacts = EXCLUDED.max_contacts
            """,
            new { Id = sipId });
    }

    /// <summary>
    /// Removes all PJSIP rows (endpoint, auth, AOR) and any queue memberships
    /// for the given agent.
    /// </summary>
    public async Task RemoveAgentAsync(
        string tenantId,
        string agentId,
        CancellationToken ct = default)
    {
        var sipId        = $"{tenantId}-agent-{agentId}";
        var sipInterface = $"PJSIP/{sipId}";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(
            "DELETE FROM ps_aors WHERE id = @Id",
            new { Id = sipId });

        await conn.ExecuteAsync(
            "DELETE FROM ps_auths WHERE id = @Id",
            new { Id = $"auth-{sipId}" });

        await conn.ExecuteAsync(
            "DELETE FROM ps_endpoints WHERE id = @Id",
            new { Id = sipId });

        await conn.ExecuteAsync(
            "DELETE FROM rt_queue_members WHERE interface = @Iface",
            new { Iface = sipInterface });
    }

    // -------------------------------------------------------------------------
    // Queue → rt_queues
    // -------------------------------------------------------------------------

    /// <summary>
    /// Upserts an Asterisk Realtime queue row for the given Platform queue.
    /// </summary>
    public async Task SyncQueueAsync(
        string tenantId,
        string queueName,
        int timeout      = 30,
        int wrapuptime   = 15,
        int servicelevel = 20,
        CancellationToken ct = default)
    {
        var rtName = $"{tenantId}-{queueName}";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync("""
            INSERT INTO rt_queues (name, strategy, timeout, wrapuptime, servicelevel)
            VALUES (@Name, 'rrmemory', @Timeout, @Wrapup, @Sla)
            ON CONFLICT (name) DO UPDATE SET timeout = EXCLUDED.timeout, wrapuptime = EXCLUDED.wrapuptime
            """,
            new { Name = rtName, Timeout = timeout, Wrapup = wrapuptime, Sla = servicelevel });
    }

    // -------------------------------------------------------------------------
    // Queue members → rt_queue_members
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adds (or replaces) the given agent as a member of the specified queue.
    /// </summary>
    public async Task AddQueueMemberAsync(
        string tenantId,
        string queueName,
        string agentId,
        string displayName,
        int penalty = 0,
        CancellationToken ct = default)
    {
        var rtQueue      = $"{tenantId}-{queueName}";
        var sipInterface = $"PJSIP/{tenantId}-agent-{agentId}";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Remove stale row first to avoid constraint violations on re-add
        await conn.ExecuteAsync(
            "DELETE FROM rt_queue_members WHERE queue_name = @Q AND interface = @I",
            new { Q = rtQueue, I = sipInterface });

        await conn.ExecuteAsync("""
            INSERT INTO rt_queue_members (membername, queue_name, interface, state_interface, penalty, paused)
            VALUES (@Name, @Queue, @Iface, @Iface, @Penalty, 0)
            """,
            new { Name = displayName, Queue = rtQueue, Iface = sipInterface, Penalty = penalty });
    }

    /// <summary>Removes the given agent from the specified queue.</summary>
    public async Task RemoveQueueMemberAsync(
        string tenantId,
        string queueName,
        string agentId,
        CancellationToken ct = default)
    {
        var rtQueue      = $"{tenantId}-{queueName}";
        var sipInterface = $"PJSIP/{tenantId}-agent-{agentId}";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(
            "DELETE FROM rt_queue_members WHERE queue_name = @Q AND interface = @I",
            new { Q = rtQueue, I = sipInterface });
    }

    /// <summary>
    /// Updates the paused flag for all queue memberships belonging to the agent.
    /// </summary>
    public async Task SyncAgentPausedAsync(
        string tenantId,
        string agentId,
        bool paused,
        CancellationToken ct = default)
    {
        var sipInterface = $"PJSIP/{tenantId}-agent-{agentId}";

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await conn.ExecuteAsync(
            "UPDATE rt_queue_members SET paused = @Paused WHERE interface = @Iface",
            new { Paused = paused ? 1 : 0, Iface = sipInterface });
    }
}
