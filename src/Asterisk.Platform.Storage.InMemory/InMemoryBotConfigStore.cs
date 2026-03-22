using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Bot;

namespace Asterisk.Platform.Storage.InMemory;

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

    public Task SaveAsync(BotConfiguration config, CancellationToken ct)
    {
        _items[(config.TenantId, config.BotId)] = config;
        return Task.CompletedTask;
    }
}
