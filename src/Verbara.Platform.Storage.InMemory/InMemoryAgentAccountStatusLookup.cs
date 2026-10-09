using Verbara.Platform.Core;
using Verbara.Platform.Identity;
using Verbara.Platform.Queues.Services;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// In-memory <see cref="IAgentAccountStatusLookup"/> (licensed-agent-metering, design D2), read through
/// <see cref="IUserStore.GetByIdsAsync"/> on every call — the read the auth hot-path cache passes
/// through, so a status write is seen by the next decision.
/// </summary>
public sealed class InMemoryAgentAccountStatusLookup : IAgentAccountStatusLookup
{
    private readonly IUserStore _users;

    public InMemoryAgentAccountStatusLookup(IUserStore users)
    {
        ArgumentNullException.ThrowIfNull(users);
        _users = users;
    }

    public async Task<IReadOnlySet<EntityId>> GetActiveUserIdsAsync(
        TenantId tenantId, IReadOnlyCollection<EntityId> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
            return new HashSet<EntityId>();

        var users = await _users.GetByIdsAsync(
            tenantId.Value, userIds.Select(id => id.Value).Distinct().ToList(), ct).ConfigureAwait(false);
        return (users ?? [])
            .Where(u => u.TenantId == tenantId && u.Status == UserStatus.Active)
            .Select(u => u.UserId)
            .ToHashSet();
    }
}
