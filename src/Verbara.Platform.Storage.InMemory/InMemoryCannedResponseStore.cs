using System.Collections.Concurrent;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Storage.InMemory;

public sealed class InMemoryCannedResponseStore : ICannedResponseStore
{
    private readonly ConcurrentDictionary<string, CannedResponse> _store = new();

    private static string Key(TenantId t, EntityId id) => $"{t.Value}:{id.Value}";

    public Task<CannedResponse?> GetByIdAsync(TenantId tenantId, EntityId responseId, CancellationToken ct)
    {
        _store.TryGetValue(Key(tenantId, responseId), out var result);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<CannedResponse>> ListByTenantAsync(TenantId tenantId, CancellationToken ct)
    {
        var items = _store.Values
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Shortcut)
            .ToList();
        return Task.FromResult<IReadOnlyList<CannedResponse>>(items);
    }

    public Task<IReadOnlyList<CannedResponse>> SearchAsync(TenantId tenantId, string query, CancellationToken ct)
    {
        var items = _store.Values
            .Where(r => r.TenantId == tenantId &&
                (r.Shortcut.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 r.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 r.Body.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 (r.Category is not null && r.Category.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                 r.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(r => r.Shortcut)
            .ToList();
        return Task.FromResult<IReadOnlyList<CannedResponse>>(items);
    }

    public Task SaveAsync(CannedResponse response, CancellationToken ct)
    {
        _store[Key(response.TenantId, response.ResponseId)] = response;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId responseId, CancellationToken ct)
    {
        _store.TryRemove(Key(tenantId, responseId), out _);
        return Task.CompletedTask;
    }
}
