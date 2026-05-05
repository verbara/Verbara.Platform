using System.Collections.Concurrent;
using Verbara.Platform.Core;
using Verbara.Platform.Surveys;

namespace Verbara.Platform.Storage.InMemory;

internal sealed class InMemorySurveyStore : ISurveyStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), Survey> _items = new();

    public Task<Survey?> GetByIdAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct)
    {
        _items.TryGetValue((tenantId, surveyId), out var item);
        return Task.FromResult(item);
    }

    public Task<IReadOnlyList<Survey>> GetActiveAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<Survey> result = _items.Values
            .Where(s => s.TenantId == tenantId && s.IsActive)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Survey>> GetAllAsync(TenantId tenantId, CancellationToken ct)
    {
        IReadOnlyList<Survey> result = _items.Values
            .Where(s => s.TenantId == tenantId)
            .ToList();

        return Task.FromResult(result);
    }

    public Task SaveAsync(Survey survey, CancellationToken ct)
    {
        _items[(survey.TenantId, survey.SurveyId)] = survey;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct)
    {
        _items.TryRemove((tenantId, surveyId), out _);
        return Task.CompletedTask;
    }
}
