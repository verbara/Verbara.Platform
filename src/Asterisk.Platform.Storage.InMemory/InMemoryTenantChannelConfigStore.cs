using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Channels.Core;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryTenantChannelConfigStore : ITenantChannelConfigStore
{
    private readonly ConcurrentDictionary<(TenantId, ChannelType), TenantChannelConfig> _items = new();

    public Task<TenantChannelConfig?> GetAsync(TenantId tenantId, ChannelType channel, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, channel), out var item);
        return Task.FromResult(item);
    }

    public Task SaveAsync(TenantChannelConfig config, CancellationToken ct)
    {
        _items[(config.TenantId, config.Channel)] = config;
        return Task.CompletedTask;
    }
}
