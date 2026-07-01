using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Typification;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// Dev/test in-memory implementation of <see cref="ITypificationSubmissionCorrectionStore"/>
/// (ADR-0034 Decision 5). One record per conversation; append-only — a second insert for a
/// conversation that already has a correction throws (mirroring the Postgres primary-key
/// violation), because the endpoint's AlreadyCorrected guard must have already rejected it.
/// </summary>
internal sealed class InMemoryTypificationSubmissionCorrectionStore : ITypificationSubmissionCorrectionStore
{
    private readonly ConcurrentDictionary<string, TypificationSubmissionCorrection> _items = new();

    private static string Key(TenantId tenantId, EntityId conversationId) =>
        $"{tenantId.Value}::{conversationId.Value}";

    public Task<TypificationSubmissionCorrection?> GetAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        _items.TryGetValue(Key(tenantId, conversationId), out var record);
        return Task.FromResult(record);
    }

    public Task InsertAsync(TypificationSubmissionCorrection record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        var key = Key(record.TenantId, record.ConversationId);
        if (!_items.TryAdd(key, record))
            throw new InvalidOperationException(
                $"A correction already exists for conversation '{record.ConversationId.Value}'.");

        return Task.CompletedTask;
    }
}
