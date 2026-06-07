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

    public async Task<ChannelCapacity?> ResolveAsync(TenantId tenantId, EntityId agentId, CancellationToken ct)
    {
        // The resolver owns the SINGLE agent read for the capacity path AND doubles as the
        // existence signal: a missing agent returns null so callers never route work to a
        // deleted/phantom agent (do NOT merge defaults for an agent that does not exist).
        var agent = await _agents.GetByIdAsync(tenantId, agentId, ct).ConfigureAwait(false);
        if (agent is null)
            return null;

        var defaults = await _defaults.GetDefaultsAsync(tenantId, ct).ConfigureAwait(false);
        var effective = agent.CapacityOverride.ToEffective(defaults);

        // W6 invariant — voice is a single-call exclusive lane regardless of any tenant default or
        // per-agent override. An agent on a voice call cannot concurrently bridge a second call until
        // the W5b ARI mixing-bridge lands (deferred north-star); pin MaxVoice to 1 until then.
        effective.MaxVoice = 1;
        return effective;
    }
}
