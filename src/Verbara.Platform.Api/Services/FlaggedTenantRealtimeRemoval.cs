using Verbara.Platform.Api.Services.SupervisorListen;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Platform.Queues.Services;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.Realtime;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Removes a flagged tenant's realtime rows by exact id through <see cref="IRealtimeSyncService"/>
/// (realtime-objects-stay-in-their-tenant, design D3): the member row of every stored agent in every stored queue,
/// every stored agent's and supervisor listen slot's endpoint, and every stored trunk. It never removes a queue row:
/// each kept queue is logged.
/// </summary>
/// <remarks>
/// The memberships are the product of every stored agent (any user status, with or without SIP credentials) and every
/// stored queue read page by page to the end, not the filtered effective view, so a manual membership beyond the first
/// page of queues, or of an agent without credentials, is removed too. Each call deletes at most one exact row and is a
/// no-op when the row is absent. Every call runs with <see cref="CancellationToken.None"/>; a failure is logged and the
/// next call still runs.
/// </remarks>
internal sealed partial class FlaggedTenantRealtimeRemoval
{
    private readonly IRealtimeSyncService _sync;
    private readonly IAgentStore _agents;
    private readonly IQueueStore _queues;
    private readonly TrunkStoreBase? _trunks;
    private readonly ISupervisorListenProfileStore? _listenProfiles;
    private readonly ILogger<FlaggedTenantRealtimeRemoval> _logger;

    public FlaggedTenantRealtimeRemoval(
        IRealtimeSyncService sync,
        IAgentStore agents,
        IQueueStore queues,
        ILogger<FlaggedTenantRealtimeRemoval> logger,
        TrunkStoreBase? trunks = null,
        ISupervisorListenProfileStore? listenProfiles = null)
    {
        _sync = sync;
        _agents = agents;
        _queues = queues;
        _logger = logger;
        _trunks = trunks;
        _listenProfiles = listenProfiles;
    }

    /// <summary>Removes <paramref name="tenantId"/>'s rows by exact id; <paramref name="reason"/> names its flag.</summary>
    public async ValueTask RemoveAsync(string tenantId, string reason, CancellationToken ct)
    {
        _ = ct; // the removal runs to the end once started: every call below uses CancellationToken.None
        var none = CancellationToken.None;
        var tid = new TenantId(tenantId);

        var agents = await ReadAsync("agents", tenantId, () => _agents.ListAllAsync(tid, none)).ConfigureAwait(false) ?? [];
        var queues = await ReadAsync("queues", tenantId, () => _queues.ListAllAsync(tid, none)).ConfigureAwait(false) ?? [];

        foreach (var queue in queues)
        {
            foreach (var agent in agents)
                await TryAsync("RemoveQueueMember", tenantId, $"{queue.Name}/{agent.AgentId.Value}",
                    () => _sync.RemoveQueueMemberAsync(tenantId, queue.Name, agent.AgentId.Value, none)).ConfigureAwait(false);
        }

        foreach (var agent in agents)
            await TryAsync("RemoveAgent", tenantId, agent.AgentId.Value,
                () => _sync.RemoveAgentAsync(tenantId, agent.AgentId.Value, none)).ConfigureAwait(false);

        if (_listenProfiles is not null)
        {
            var profiles = await ReadAsync("listen slots", tenantId,
                () => _listenProfiles.ListWithCredentialsAsync(tid, none)).ConfigureAwait(false) ?? [];
            foreach (var profile in profiles)
            {
                var slot = SupervisorListenEndpointProvisioner.Slot(profile.UserId);
                await TryAsync("RemoveAgent", tenantId, slot, () => _sync.RemoveAgentAsync(tenantId, slot, none)).ConfigureAwait(false);
            }
        }

        if (_trunks is not null)
        {
            // RemoveTrunkAsync ignores its tenant argument: only ids read from this tenant's own trunk store are passed.
            var trunks = await ReadAsync("trunks", tenantId, async () => await _trunks.ListAsync(tenantId, none).ConfigureAwait(false))
                .ConfigureAwait(false) ?? [];
            foreach (var trunk in trunks)
                await TryAsync("RemoveTrunk", tenantId, trunk.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    () => _sync.RemoveTrunkAsync(tenantId, trunk.Id, none)).ConfigureAwait(false);
        }

        foreach (var queue in queues)
            LogKeptQueue(_logger, $"{tenantId}-{queue.Name}", tenantId, reason);
    }

    private async Task<IReadOnlyList<T>?> ReadAsync<T>(string what, string tenantId, Func<Task<IReadOnlyList<T>>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogReadFailed(_logger, what, tenantId, ex);
            return null;
        }
    }

    private async Task TryAsync(string operation, string tenantId, string target, Func<ValueTask> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRemovalFailed(_logger, operation, target, tenantId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kept realtime queue {Queue} of tenant {TenantId}: the tenant id is flagged ({Reason})")]
    internal static partial void LogKeptQueue(ILogger logger, string queue, string tenantId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read the {What} of flagged tenant {TenantId}; their realtime rows were not removed")]
    private static partial void LogReadFailed(ILogger logger, string what, string tenantId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Operation} {Target} of flagged tenant {TenantId} failed")]
    private static partial void LogRemovalFailed(ILogger logger, string operation, string target, string tenantId, Exception ex);
}
