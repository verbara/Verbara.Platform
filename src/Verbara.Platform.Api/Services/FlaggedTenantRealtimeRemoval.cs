using Verbara.Platform.Queues;
using Verbara.Sdk.Pro.Dialer.Routing;
using Verbara.Sdk.Pro.Realtime;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Removes a flagged tenant's realtime rows by exact id through <see cref="IRealtimeSyncService"/>
/// (realtime-objects-stay-in-their-tenant, design D3): the member row of every stored agent in every stored queue,
/// every stored agent's and supervisor listen slot's endpoint, and every stored trunk. It never removes a queue row:
/// each kept queue is logged.
/// </summary>
internal sealed class FlaggedTenantRealtimeRemoval
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
    public ValueTask RemoveAsync(string tenantId, string reason, CancellationToken ct) => throw new NotImplementedException();
}
