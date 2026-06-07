using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Services;

/// <summary>
/// Default <see cref="IAgentCapacityResolver"/>: loads the agent + the tenant defaults and merges
/// the sparse per-agent <see cref="ChannelCapacityOverride"/> over them (override wins field-by-field).
/// Stateless — both reads go through Singleton stores, so the resolver is registered Singleton too.
/// </summary>
public sealed class AgentCapacityResolver : IAgentCapacityResolver
{
    private readonly IAgentStore _agents;
    private readonly ICapacityDefaultsProvider _defaults;

    public AgentCapacityResolver(IAgentStore agents, ICapacityDefaultsProvider defaults)
    {
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(defaults);
        _agents = agents;
        _defaults = defaults;
    }

    public async Task<ChannelCapacity> ResolveAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        var defaults = await _defaults.GetDefaultsAsync(tenantId, ct).ConfigureAwait(false);
        var agent = await _agents.GetByIdAsync(tenantId, agentId, ct).ConfigureAwait(false);

        // A missing agent resolves to the bare tenant defaults (an all-null override is the
        // "inherit everything" identity merge), so the per-channel maxima are still well-defined.
        var over = agent?.CapacityOverride ?? new ChannelCapacityOverride();
        var effective = over.ToEffective(defaults);

        // W6 invariant — voice is a single-call exclusive lane regardless of any tenant default or
        // per-agent override. An agent on a voice call cannot concurrently bridge a second call until
        // the W5b ARI mixing-bridge lands (deferred north-star); pin MaxVoice to 1 until then.
        effective.MaxVoice = 1;
        return effective;
    }
}
