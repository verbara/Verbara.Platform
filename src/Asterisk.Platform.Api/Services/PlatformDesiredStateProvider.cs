using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;
using Asterisk.Sdk.Pro.Dialer.Routing;
using Asterisk.Sdk.Pro.Dialer.Models;
using Asterisk.Sdk.Pro.Realtime.Engine;
using Asterisk.Sdk.Pro.Realtime.Models;

namespace Asterisk.Platform.Api.Services;

/// <summary>
/// Implements <see cref="IDesiredStateProvider"/> by reading from Platform stores,
/// feeding the 5-phase reconciler with expected agents, queues, queue members, and trunks.
/// </summary>
internal sealed class PlatformDesiredStateProvider : IDesiredStateProvider
{
    private readonly IConfiguration _configuration;
    private readonly IAgentStore _agentStore;
    private readonly IQueueStore _queueStore;
    private readonly TrunkStoreBase _trunkStore;
    private readonly QueueMembershipService _membershipService;

    public PlatformDesiredStateProvider(
        IConfiguration configuration,
        IAgentStore agentStore,
        IQueueStore queueStore,
        TrunkStoreBase trunkStore,
        QueueMembershipService membershipService)
    {
        _configuration = configuration;
        _agentStore = agentStore;
        _queueStore = queueStore;
        _trunkStore = trunkStore;
        _membershipService = membershipService;
    }

    public ValueTask<IReadOnlyList<string>> GetActiveTenantIdsAsync(CancellationToken ct = default)
    {
        var tenantIds = _configuration.GetSection("Realtime:TenantIds").Get<string[]>() ?? ["demo"];
        return new ValueTask<IReadOnlyList<string>>(tenantIds);
    }

    public async ValueTask<IReadOnlyList<AgentSyncRequest>> GetExpectedAgentsAsync(
        string tenantId, CancellationToken ct = default)
    {
        var tid = new TenantId(tenantId);
        var agents = await _agentStore.ListAsync(tid, new AgentQuery { PageSize = 1000 }, ct);
        return agents.Items
            .Where(a => !string.IsNullOrEmpty(a.Extension) && !string.IsNullOrEmpty(a.SipPassword))
            .Select(a => new AgentSyncRequest
            {
                AgentId = a.AgentId.Value,
                DisplayName = a.DisplayName,
                Extension = a.Extension!,
                SipPassword = a.SipPassword!,
            })
            .ToList();
    }

    public async ValueTask<IReadOnlyList<QueueSyncRequest>> GetExpectedQueuesAsync(
        string tenantId, CancellationToken ct = default)
    {
        var tid = new TenantId(tenantId);
        var queues = await _queueStore.ListAsync(tid, new PagedQuery(1, 1000), ct);
        return queues.Items
            .Where(q => q.IsActive)
            .Select(q => new QueueSyncRequest
            {
                QueueName = q.Name,
                Options = new RealtimeQueueOptions
                {
                    Wrapuptime = q.WrapUp.DefaultWrapUpSeconds,
                    Servicelevel = q.SlaTargets?.AnswerWithinSeconds ?? 20,
                    Maxlen = q.MaxWaiting ?? 0,
                },
            })
            .ToList();
    }

    public async ValueTask<IReadOnlyList<QueueMemberSyncRequest>> GetExpectedQueueMembersAsync(
        string tenantId, CancellationToken ct = default)
    {
        var effective = await _membershipService.ComputeEffectiveMembersAsync(tenantId, ct);
        return effective
            .Select(m => new QueueMemberSyncRequest
            {
                QueueName = m.QueueName,
                AgentId = m.AgentId,
                DisplayName = m.DisplayName,
                Penalty = m.Penalty,
            })
            .ToList();
    }

    public async ValueTask<IReadOnlyList<TrunkSyncRequest>> GetExpectedTrunksAsync(
        string tenantId, CancellationToken ct = default)
    {
        var trunks = await _trunkStore.ListActiveAsync(tenantId, ct);
        return trunks
            .Where(t => !string.IsNullOrEmpty(t.AuthUsername))
            .Select(t => new TrunkSyncRequest
            {
                TrunkName = t.Name,
                Trunk = t,
            })
            .ToList();
    }
}
