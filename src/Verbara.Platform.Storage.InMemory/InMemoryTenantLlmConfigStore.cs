using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Llm;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// Dev/test in-memory implementation of <see cref="ITenantLlmConfigStore"/>. Stores the
/// decrypted API key as-is (NO encryption — dev/test only; the Postgres store encrypts at rest).
/// </summary>
internal sealed class InMemoryTenantLlmConfigStore : ITenantLlmConfigStore
{
    private readonly ConcurrentDictionary<string, TenantLlmConfig> _items = new();

    public Task<TenantLlmConfig?> GetAsync(EntityId tenantId, CancellationToken ct)
    {
        _items.TryGetValue(tenantId.Value, out var config);
        return Task.FromResult(config);
    }

    public Task UpsertAsync(TenantLlmConfig config, CancellationToken ct)
    {
        _items[config.TenantId.Value] = config;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(EntityId tenantId, CancellationToken ct)
    {
        _items.TryRemove(tenantId.Value, out _);
        return Task.CompletedTask;
    }
}
