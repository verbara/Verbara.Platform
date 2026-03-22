using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Queues;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryTeamStore : ITeamStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Team> _items = new();

    public Task<Team?> GetByIdAsync(TenantId tenantId, EntityId teamId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, teamId), out var item);
        return Task.FromResult(item);
    }

    public Task<PagedResult<Team>> ListAsync(TenantId tenantId, PagedQuery query, CancellationToken ct)
    {
        var filtered = _items.Values
            .Where(t => t.TenantId == tenantId)
            .ToList();

        var totalCount = filtered.Count;
        var items = filtered.Skip(query.Offset).Take(query.PageSize).ToList();

        return Task.FromResult(new PagedResult<Team>(items, totalCount, query.Page, query.PageSize));
    }

    public Task SaveAsync(Team team, CancellationToken ct)
    {
        _items[(team.TenantId, team.TeamId)] = team;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId teamId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, teamId), out _);
        return Task.CompletedTask;
    }
}
