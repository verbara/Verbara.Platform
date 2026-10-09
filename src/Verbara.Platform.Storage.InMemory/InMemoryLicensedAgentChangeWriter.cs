using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Licensing;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// The in-memory twin of the licensed-agent write path (licensed-agent-ledger, design D5), with the same
/// outcomes as the Postgres writer: a change and its ledger row are both visible or neither is. Writes are
/// serialised (the head lock's role), the chain is opened before the domain write (so an anchor baselines
/// the state before the change), and the staged rows are committed only after the domain write succeeded.
/// </summary>
/// <remarks>
/// The stores passed in must be the undecorated inner stores: the realtime side effects are the caller's,
/// after the commit, exactly as in Postgres mode.
/// </remarks>
public sealed class InMemoryLicensedAgentChangeWriter : ILicensedAgentChangeWriter, ILicensedUserChangeWriter, IDisposable
{
    private readonly IAgentStore _agents;
    private readonly IUserStore _users;
    private readonly IConversationStore _conversations;
    private readonly InMemoryLicenseAgentLedger _ledger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public InMemoryLicensedAgentChangeWriter(
        IAgentStore agents, IUserStore users, IConversationStore conversations, InMemoryLicenseAgentLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(ledger);
        _agents = agents;
        _users = users;
        _conversations = conversations;
        _ledger = ledger;
    }

    public async Task CommitAgentCreatedAsync(Agent agent, string? actorUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var staged = await BeginAsync(agent.TenantId, ct).ConfigureAwait(false);
            await _agents.SaveAsync(agent, ct).ConfigureAwait(false);
            staged.Append(LicenseAgentEventKinds.AgentCreated, agent.AgentId.Value, agent.UserId.Value, actorUserId, null, null,
                await IsActiveAsync(agent.TenantId, agent.UserId, ct).ConfigureAwait(false));
            staged.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> CommitAgentDeletedAsync(TenantId tenantId, EntityId agentId, string? actorUserId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await _agents.GetByIdAsync(tenantId, agentId, ct).ConfigureAwait(false) is not { } agent)
                return false;
            var staged = await BeginAsync(tenantId, ct).ConfigureAwait(false);
            await _agents.DeleteAsync(tenantId, agentId, ct).ConfigureAwait(false);
            staged.Append(LicenseAgentEventKinds.AgentDeleted, agentId.Value, agent.UserId.Value, actorUserId, null, null, false);
            staged.Commit();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CommitOwnershipAsync(Conversation conversation, OwnershipChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(change);
        if (conversation.Owner is not { Kind: ConversationOwnerKind.Agent, OwnerId: { } receiver })
            throw new ArgumentException("An ownership change commits a conversation owned by an agent.", nameof(conversation));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var staged = await BeginAsync(conversation.TenantId, ct).ConfigureAwait(false);
            await _conversations.SaveAsync(conversation, ct).ConfigureAwait(false);
            var agent = await _agents.GetByIdAsync(conversation.TenantId, receiver, ct).ConfigureAwait(false);
            var counted = agent is not null && await IsActiveAsync(conversation.TenantId, agent.UserId, ct).ConfigureAwait(false);
            staged.Append(change.LedgerKind, receiver.Value, agent?.UserId.Value, change.ActorUserId,
                conversation.ConversationId.Value, null, counted);
            staged.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdminFieldsWriteResult> CommitUserStatusChangedAsync(
        TenantId tenantId, EntityId userId, AdminFieldsChange change, DateTimeOffset updatedAt, string? updatedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var staged = change.Status is null ? null : await BeginAsync(tenantId, ct).ConfigureAwait(false);
            var result = await _users.UpdateAdminFieldsAsync(tenantId, userId, change, updatedAt, updatedBy, ct).ConfigureAwait(false);
            if (staged is not null
                && result is { Outcome: AdminFieldsWriteOutcome.Written, Previous: { } previous, User: { } stored }
                && previous.Status != stored.Status
                && await _agents.GetByUserIdAsync(tenantId, userId, ct).ConfigureAwait(false) is { } agent)
            {
                staged.Append(LicenseAgentEventKinds.UserStatusChanged, agent.AgentId.Value, userId.Value, updatedBy, null,
                    stored.Status.ToString(), stored.Status == UserStatus.Active);
            }

            staged?.Commit();
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LicensedUserDeletion> CommitUserDeletedAsync(
        TenantId tenantId, EntityId userId, string? actorUserId, bool deleteOwnedAgent, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await _users.GetByIdAsync(tenantId, userId, ct).ConfigureAwait(false) is not { } user)
                return LicensedUserDeletion.NotFound;

            var agent = await _agents.GetByUserIdAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (agent is not null && !deleteOwnedAgent)
                return LicensedUserDeletion.RefusedOwnsAgent;

            var staged = await BeginAsync(tenantId, ct).ConfigureAwait(false);
            if (agent is not null)
            {
                await _agents.DeleteAsync(tenantId, agent.AgentId, ct).ConfigureAwait(false);
                staged.Append(LicenseAgentEventKinds.AgentDeleted, agent.AgentId.Value, userId.Value, actorUserId, null, null, false);
            }

            await _users.DeleteAsync(tenantId, userId, ct).ConfigureAwait(false);
            if (agent is not null)
            {
                staged.Append(LicenseAgentEventKinds.UserDeleted, agent.AgentId.Value, userId.Value, actorUserId, null,
                    user.Status.ToString(), false);
            }

            staged.Commit();
            return new LicensedUserDeletion(true, false, agent?.AgentId.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<InMemoryLicenseAgentLedger.Staged> BeginAsync(TenantId tenantId, CancellationToken ct)
    {
        // The baseline is read only when this call anchors the chain; it reflects the state before the change.
        var agents = await _agents.ListAsync(tenantId, new AgentQuery { Page = 1, PageSize = int.MaxValue }, ct).ConfigureAwait(false);
        var baseline = new List<(string, string, bool)>();
        foreach (var agent in agents.Items.OrderBy(a => a.AgentId.Value, StringComparer.Ordinal))
            baseline.Add((agent.AgentId.Value, agent.UserId.Value, await IsActiveAsync(tenantId, agent.UserId, ct).ConfigureAwait(false)));
        return _ledger.Begin(tenantId.Value, () => baseline);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<bool> IsActiveAsync(TenantId tenantId, EntityId userId, CancellationToken ct) =>
        await _users.GetByIdAsync(tenantId, userId, ct).ConfigureAwait(false) is { Status: UserStatus.Active };
}
