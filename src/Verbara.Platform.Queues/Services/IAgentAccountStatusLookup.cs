using Verbara.Platform.Core;

namespace Verbara.Platform.Queues.Services;

/// <summary>
/// Answers whether the user that owns an agent is <c>Active</c> (licensed-agent-metering, design D2).
/// Routing eligibility, the round-robin sticky path, switchboard ownership changes and the PJSIP
/// desired state all ask it, so a user's account status governs whether that user's agent can receive
/// work and keep a phone endpoint.
/// </summary>
/// <remarks>
/// Queues does not reference Identity, so the seam speaks in user ids only. An implementation reads the
/// current status on every call, with no cache: a status write takes effect on the next decision. A
/// user id that names no user in the tenant is not active.
/// </remarks>
public interface IAgentAccountStatusLookup
{
    /// <summary>
    /// Returns the subset of <paramref name="userIds"/> whose user exists in <paramref name="tenantId"/>
    /// with status <c>Active</c>.
    /// </summary>
    Task<IReadOnlySet<EntityId>> GetActiveUserIdsAsync(
        TenantId tenantId, IReadOnlyCollection<EntityId> userIds, CancellationToken ct);

    /// <summary>Whether the user <paramref name="userId"/> exists in <paramref name="tenantId"/> and is <c>Active</c>.</summary>
    async Task<bool> IsActiveAsync(TenantId tenantId, EntityId userId, CancellationToken ct) =>
        (await GetActiveUserIdsAsync(tenantId, [userId], ct).ConfigureAwait(false)).Contains(userId);
}

/// <summary>Helpers over <see cref="IAgentAccountStatusLookup"/> and <see cref="IAgentStore"/>.</summary>
public static class AgentAccountStatusLookupExtensions
{
    /// <summary>The page size <see cref="ListAllAsync"/> reads with.</summary>
    public const int EnumerationPageSize = 1000;

    /// <summary>
    /// Keeps the agents of <paramref name="agents"/> whose user is <c>Active</c>, in their order, with one
    /// lookup for the whole set.
    /// </summary>
    public static async Task<IReadOnlyList<Agent>> WhereUserActiveAsync(
        this IAgentAccountStatusLookup lookup, TenantId tenantId, IReadOnlyList<Agent> agents, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(agents);
        if (agents.Count == 0)
            return agents;

        var userIds = agents.Select(a => a.UserId).Distinct().ToList();
        var active = await lookup.GetActiveUserIdsAsync(tenantId, userIds, ct).ConfigureAwait(false);
        return agents.Where(a => active.Contains(a.UserId)).ToList();
    }

    /// <summary>
    /// Every agent of <paramref name="tenantId"/>, read page by page until the store's total is reached,
    /// so no caller is silently capped at one page.
    /// </summary>
    public static async Task<IReadOnlyList<Agent>> ListAllAsync(
        this IAgentStore store, TenantId tenantId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var all = new List<Agent>();
        for (var page = 1; ; page++)
        {
            var result = await store.ListAsync(
                tenantId, new AgentQuery { Page = page, PageSize = EnumerationPageSize }, ct).ConfigureAwait(false);
            all.AddRange(result.Items);
            if (result.Items.Count < EnumerationPageSize || all.Count >= result.TotalCount)
                return all;
        }
    }
}
