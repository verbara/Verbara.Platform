using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Storage.InMemory;

/// <summary>
/// ConcurrentDictionary-backed <see cref="ICsatTemplateStore"/> (csat-runner Phase E).
/// Mirrors the <c>PostgresCsatTemplateStore</c> query surface so the admin CRUD endpoints
/// and the <c>CsatTemplateProvider</c> fallback chain run container-free in the Testing
/// environment (parity with <c>InMemorySurveyStore</c>).
/// </summary>
internal sealed class InMemoryCsatTemplateStore : ICsatTemplateStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), CsatTemplateEntry> _items = new();

    public Task<IReadOnlyList<CsatTemplateEntry>> GetAllAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<CsatTemplateEntry> result = _items.Values
            .Where(t => t.TenantId == tenantId)
            .OrderBy(t => t.Channel, StringComparer.Ordinal)
            .ThenBy(t => t.Locale, StringComparer.Ordinal)
            .ThenBy(t => t.TemplateId.Value, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<CsatTemplateEntry?> GetByIdAsync(TenantId tenantId, EntityId templateId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, templateId), out var item);
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<CsatTemplateEntry>> GetByChannelAndLocaleAsync(
        TenantId tenantId, string channel, string locale, CancellationToken ct)
    {
        IReadOnlyList<CsatTemplateEntry> result = _items.Values
            .Where(t => t.TenantId == tenantId
                        && string.Equals(t.Channel, channel, StringComparison.Ordinal)
                        && string.Equals(t.Locale, locale, StringComparison.Ordinal))
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.TemplateId.Value, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<CsatTemplateEntry>> GetDefaultsByChannelAsync(
        TenantId tenantId, string channel, CancellationToken ct)
    {
        IReadOnlyList<CsatTemplateEntry> result = _items.Values
            .Where(t => t.TenantId == tenantId
                        && string.Equals(t.Channel, channel, StringComparison.Ordinal)
                        && t.IsDefault)
            .OrderBy(t => t.Locale, StringComparer.Ordinal)
            .ThenBy(t => t.TemplateId.Value, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAsync(CsatTemplateEntry entry, CancellationToken ct)
    {
        _items[(entry.TenantId, entry.TemplateId)] = entry;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId templateId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, templateId), out _);
        return Task.CompletedTask;
    }
}
