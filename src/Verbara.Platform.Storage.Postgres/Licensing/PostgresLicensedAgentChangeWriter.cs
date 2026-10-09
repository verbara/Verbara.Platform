using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Licensing;
using Verbara.Platform.Storage.Postgres.Stores;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Licensing;

/// <summary>
/// The licensed-agent write path in Postgres (licensed-agent-ledger, design D5): each method opens one
/// connection and one transaction, locks the tenant's chain head, makes the domain write through the store's
/// transaction overload, appends the ledger row and commits. A failure anywhere rolls both back.
/// </summary>
/// <remarks>
/// The head is locked before the domain write, so a chain this call anchors baselines the state before the
/// change, and the countability read inside the transaction cannot be moved by another writer of the same
/// tenant. The realtime side effects are the caller's, after the commit.
/// </remarks>
internal sealed class PostgresLicensedAgentChangeWriter : ILicensedAgentChangeWriter, ILicensedUserChangeWriter
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresUserStore _users;
    private readonly LicenseAgentLedger _ledger;

    public PostgresLicensedAgentChangeWriter(NpgsqlDataSource dataSource, PostgresUserStore users, LicenseAgentLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(ledger);
        _dataSource = dataSource;
        _users = users;
        _ledger = ledger;
    }

    public async Task CommitAgentCreatedAsync(Agent agent, string? actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var chain = await _ledger.LockAsync(conn, tx, agent.TenantId.Value, ct).ConfigureAwait(false);
        await PostgresAgentStore.InsertAsync(conn, tx, agent, ct).ConfigureAwait(false);
        var counted = await LicenseAgentLedger.IsCountedAsync(conn, tx, agent.TenantId.Value, agent.AgentId.Value, ct).ConfigureAwait(false);
        await _ledger.AppendEventAsync(conn, tx, chain,
            new LicenseAgentChange(LicenseAgentEventKinds.AgentCreated, agent.AgentId.Value, agent.UserId.Value,
                actorUserId, null, null, counted), ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> CommitAgentDeletedAsync(TenantId tenantId, EntityId agentId, string? actorUserId, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var chain = await _ledger.LockAsync(conn, tx, tenantId.Value, ct).ConfigureAwait(false);
        if (!await DeleteAgentAsync(conn, tx, chain, tenantId, agentId, actorUserId, ct).ConfigureAwait(false))
            return false;

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task CommitOwnershipAsync(Conversation conversation, OwnershipChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(change);
        if (conversation.Owner is not { Kind: ConversationOwnerKind.Agent, OwnerId: { } receiver })
            throw new ArgumentException("An ownership change commits a conversation owned by an agent.", nameof(conversation));

        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var tenantId = conversation.TenantId.Value;
        var chain = await _ledger.LockAsync(conn, tx, tenantId, ct).ConfigureAwait(false);
        await PostgresConversationStore.SaveAsync(conn, tx, conversation, ct).ConfigureAwait(false);
        var userId = await AgentUserIdAsync(conn, tx, tenantId, receiver.Value, ct).ConfigureAwait(false);
        var counted = await LicenseAgentLedger.IsCountedAsync(conn, tx, tenantId, receiver.Value, ct).ConfigureAwait(false);
        await _ledger.AppendEventAsync(conn, tx, chain,
            new LicenseAgentChange(change.LedgerKind, receiver.Value, userId, change.ActorUserId,
                conversation.ConversationId.Value, null, counted), ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<AdminFieldsWriteResult> CommitUserStatusChangedAsync(
        TenantId tenantId, EntityId userId, AdminFieldsChange change, DateTimeOffset updatedAt, string? updatedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        // A change that writes no status is not a counted change: no chain is locked or anchored for it.
        var chain = change.Status is null ? null : await _ledger.LockAsync(conn, tx, tenantId.Value, ct).ConfigureAwait(false);
        var result = await _users.UpdateAdminFieldsAsync(conn, tx, tenantId, userId, change, updatedAt, updatedBy, ct).ConfigureAwait(false);

        if (chain is not null
            && result is { Outcome: AdminFieldsWriteOutcome.Written, Previous: { } previous, User: { } stored }
            && previous.Status != stored.Status
            && await OwnedAgentIdAsync(conn, tx, tenantId.Value, userId.Value, ct).ConfigureAwait(false) is { } agentId)
        {
            var counted = await LicenseAgentLedger.IsCountedAsync(conn, tx, tenantId.Value, agentId, ct).ConfigureAwait(false);
            await _ledger.AppendEventAsync(conn, tx, chain,
                new LicenseAgentChange(LicenseAgentEventKinds.UserStatusChanged, agentId, userId.Value, updatedBy, null,
                    stored.Status.ToString(), counted), ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    public async Task<LicensedUserDeletion> CommitUserDeletedAsync(
        TenantId tenantId, EntityId userId, string? actorUserId, bool deleteOwnedAgent, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        var chain = await _ledger.LockAsync(conn, tx, tenantId.Value, ct).ConfigureAwait(false);
        if (await LockUserStatusAsync(conn, tx, tenantId.Value, userId.Value, ct).ConfigureAwait(false) is not { } lastStatus)
            return LicensedUserDeletion.NotFound;

        var agentId = await OwnedAgentIdAsync(conn, tx, tenantId.Value, userId.Value, ct).ConfigureAwait(false);
        if (agentId is not null && !deleteOwnedAgent)
            return LicensedUserDeletion.RefusedOwnsAgent;

        if (agentId is not null)
            await DeleteAgentAsync(conn, tx, chain, tenantId, EntityId.From(agentId), actorUserId, ct).ConfigureAwait(false);

        await PostgresUserStore.DeleteAsync(conn, tx, tenantId, userId, ct).ConfigureAwait(false);
        if (agentId is not null)
        {
            await _ledger.AppendEventAsync(conn, tx, chain,
                new LicenseAgentChange(LicenseAgentEventKinds.UserDeleted, agentId, userId.Value, actorUserId, null,
                    lastStatus.ToString(), Counted: false), ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new LicensedUserDeletion(true, false, agentId);
    }

    private async Task<bool> DeleteAgentAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, LicenseAgentChainCursor chain, TenantId tenantId, EntityId agentId,
        string? actorUserId, CancellationToken ct)
    {
        var userId = await AgentUserIdAsync(conn, tx, tenantId.Value, agentId.Value, ct).ConfigureAwait(false);
        if (userId is null)
            return false;

        await PostgresAgentStore.DeleteAsync(conn, tx, tenantId, agentId, ct).ConfigureAwait(false);
        await _ledger.AppendEventAsync(conn, tx, chain,
            new LicenseAgentChange(LicenseAgentEventKinds.AgentDeleted, agentId.Value, userId, actorUserId, null, null,
                Counted: false), ct).ConfigureAwait(false);
        return true;
    }

    private static async Task<string?> AgentUserIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, string agentId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT user_id FROM agents WHERE tenant_id = @TenantId AND agent_id = @AgentId FOR UPDATE", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId });
        cmd.Parameters.Add(new NpgsqlParameter("AgentId", NpgsqlDbType.Text) { Value = agentId });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    // The agent the user owns (at most one, ux_agents_tenant_user), locked for the rest of the transaction.
    private static async Task<string?> OwnedAgentIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, string userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT agent_id FROM agents WHERE tenant_id = @TenantId AND user_id = @UserId ORDER BY agent_id LIMIT 1 FOR UPDATE",
            conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId });
        cmd.Parameters.Add(new NpgsqlParameter("UserId", NpgsqlDbType.Text) { Value = userId });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    private static async Task<UserStatus?> LockUserStatusAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, string userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT status FROM users WHERE tenant_id = @TenantId AND user_id = @UserId FOR UPDATE", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", NpgsqlDbType.Text) { Value = tenantId });
        cmd.Parameters.Add(new NpgsqlParameter("UserId", NpgsqlDbType.Text) { Value = userId });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is int status ? (UserStatus)status : null;
    }
}
