using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.Dialer.Models;
using Verbara.Sdk.Pro.Realtime;
using Verbara.Sdk.Pro.Realtime.Engine;
using Verbara.Sdk.Pro.Realtime.Models;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Implements <see cref="IDesiredStateProvider"/> by reading from Platform stores,
/// feeding the 5-phase reconciler with expected agents, queues, queue members, and trunks.
/// </summary>
/// <remarks>
/// licensed-agent-metering (D2): an agent whose user is not <c>Active</c>, or does not exist, is left
/// out of the expected agents and the expected queue members, so the reconciler removes its PJSIP
/// endpoint and its <c>queue_members</c> rows and re-creates them once the user is Active again. Every
/// agent of the tenant is enumerated, with no page cap.
/// <para>
/// supervisor-silent-listen (design D3): the expected agents also carry one reserved <c>sup-{userId}</c> slot per
/// Active user whose listen endpoint has credentials, on the tenant's <c>supervisor-listen</c> profile. Pro's reconciler
/// then keeps that endpoint, and removes it when the slot stops being listed (user not Active, deleted or purged) — for
/// a tenant it serves (<c>Realtime:TenantIds</c>), the same precondition agent endpoints have. Without the listen
/// profile no slot is listed: the endpoint would otherwise inherit the default, dialable context.
/// </para>
/// </remarks>
internal sealed class PlatformDesiredStateProvider : IDesiredStateProvider
{
    private readonly IConfiguration _configuration;
    private readonly IAgentStore _agentStore;
    private readonly IQueueStore _queueStore;
    private readonly TrunkStoreBase _trunkStore;
    private readonly QueueMembershipService _membershipService;
    private readonly IAgentAccountStatusLookup _accountStatus;
    private readonly ISupervisorListenProfileStore? _listenProfiles;
    private readonly EndpointProfileStoreBase? _endpointProfiles;
    private readonly IUserStore? _users;

    public PlatformDesiredStateProvider(
        IConfiguration configuration,
        IAgentStore agentStore,
        IQueueStore queueStore,
        TrunkStoreBase trunkStore,
        QueueMembershipService membershipService,
        IAgentAccountStatusLookup accountStatus,
        ISupervisorListenProfileStore? listenProfiles = null,
        EndpointProfileStoreBase? endpointProfiles = null,
        IUserStore? users = null)
    {
        _listenProfiles = listenProfiles;
        _endpointProfiles = endpointProfiles;
        _users = users;
        _configuration = configuration;
        _agentStore = agentStore;
        _queueStore = queueStore;
        _trunkStore = trunkStore;
        _membershipService = membershipService;
        _accountStatus = accountStatus;
    }

    public ValueTask<IReadOnlyList<string>> GetActiveTenantIdsAsync(CancellationToken ct = default)
    {
        var section = _configuration.GetSection("Realtime:TenantIds");
        var tenantIds = section.Exists()
            ? section.GetChildren().Select(c => c.Value!).Where(v => v is not null).ToArray()
            : (string[])["demo"];
        return new ValueTask<IReadOnlyList<string>>(tenantIds);
    }

    public async ValueTask<IReadOnlyList<AgentSyncRequest>> GetExpectedAgentsAsync(
        string tenantId, CancellationToken ct = default)
    {
        var tid = new TenantId(tenantId);
        var agents = await _accountStatus.WhereUserActiveAsync(tid, await _agentStore.ListAllAsync(tid, ct), ct);
        var expected = agents
            .Where(a => !string.IsNullOrEmpty(a.Extension) && !string.IsNullOrEmpty(a.SipPassword))
            .Select(a => new AgentSyncRequest
            {
                AgentId = a.AgentId.Value,
                DisplayName = a.DisplayName,
                Extension = a.Extension!,
                SipPassword = a.SipPassword!,
            })
            .ToList();
        expected.AddRange(await GetExpectedListenEndpointsAsync(tid, ct));
        return expected;
    }

    /// <summary>supervisor-silent-listen (design D3): one <c>sup-{userId}</c> slot per Active user with listen credentials.</summary>
    private async Task<IReadOnlyList<AgentSyncRequest>> GetExpectedListenEndpointsAsync(TenantId tid, CancellationToken ct)
    {
        if (_listenProfiles is null)
            return [];
        var withCredentials = await _listenProfiles.ListWithCredentialsAsync(tid, ct);
        if (withCredentials.Count == 0)
            return [];
        if (await SupervisorListenEndpointProvisioner.FindProfileIdAsync(_endpointProfiles, tid.Value, ct) is not { } profileId)
            return [];

        var active = await _accountStatus.GetActiveUserIdsAsync(tid, [.. withCredentials.Select(p => p.UserId)], ct);
        var slots = new List<AgentSyncRequest>();
        foreach (var profile in withCredentials.Where(p => active.Contains(p.UserId)))
        {
            var user = _users is null ? null : await _users.GetByIdAsync(tid, profile.UserId, ct);
            slots.Add(new AgentSyncRequest
            {
                AgentId = SupervisorListenEndpointProvisioner.Slot(profile.UserId),
                DisplayName = string.IsNullOrWhiteSpace(user?.DisplayName) ? profile.UserId.Value : user.DisplayName,
                Extension = profile.SipExtension!,
                SipPassword = profile.SipPassword!,
                ProfileId = profileId,
            });
        }

        return slots;
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
        var tid = new TenantId(tenantId);
        var effective = await _membershipService.ComputeEffectiveMembersAsync(tenantId, ct);
        if (effective.Count == 0)
            return [];

        // A member whose agent's user is not Active is deprovisioned, not paused: leaving it out makes
        // the reconciler delete its queue_members row (realtime-queue-member-sync).
        var agents = await _agentStore.GetByIdsAsync(
            tid, effective.Select(m => EntityId.From(m.AgentId)).Distinct().ToList(), ct);
        var routable = (await _accountStatus.WhereUserActiveAsync(tid, agents, ct))
            .Select(a => a.AgentId.Value)
            .ToHashSet(StringComparer.Ordinal);
        return effective
            .Where(m => routable.Contains(m.AgentId))
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
