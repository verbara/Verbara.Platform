using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Bot;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemoryBotConfigStore : IBotConfigStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), BotConfiguration> _items = new();

    public Task<BotConfiguration?> GetByIdAsync(TenantId tenantId, EntityId botId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, botId), out var item);
        return Task.FromResult(item);
    }

    public Task<BotConfiguration?> GetDefaultAsync(TenantId tenantId, CancellationToken ct)
    {
        var result = _items.Values.FirstOrDefault(b =>
            b.TenantId == tenantId &&
            b.IsActive);

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<BotConfiguration>> ListAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<BotConfiguration> result = _items.Values
            .Where(b => b.TenantId == tenantId)
            .OrderBy(b => b.CreatedAt)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task SaveAsync(BotConfiguration config, CancellationToken ct)
    {
        _items.AddOrUpdate(
            (config.TenantId, config.BotId),
            _ =>
            {
                if (config.CreatedAt == default)
                    config.CreatedAt = DateTimeOffset.UtcNow;
                return config;
            },
            (_, existing) =>
            {
                // Preserve original CreatedAt on update.
                config.CreatedAt = existing.CreatedAt;
                return config;
            });
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(TenantId tenantId, EntityId botId, CancellationToken ct)
    {
        var removed = _items.TryRemove((tenantId, botId), out _);
        return Task.FromResult(removed);
    }
}
