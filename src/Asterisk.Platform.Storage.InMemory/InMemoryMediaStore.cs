using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Media;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemoryMediaStore : IMediaStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), MediaFile> _items = new();

    public Task<MediaFile?> GetByIdAsync(TenantId tenantId, EntityId fileId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, fileId), out var item);
        return Task.FromResult(item);
    }

    public Task SaveAsync(MediaFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        _items[(file.TenantId, file.FileId)] = file;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId fileId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, fileId), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MediaFile>> GetByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        IReadOnlyList<MediaFile> result = _items.Values
            .Where(f => f.TenantId == tenantId && f.ConversationId == conversationId)
            .ToList()
            .AsReadOnly();

        return Task.FromResult(result);
    }
}
