using System.Collections.Concurrent;
using Asterisk.Platform.Core;
using Asterisk.Platform.Surveys;

namespace Asterisk.Platform.Storage.InMemory;

internal sealed class InMemorySurveyResponseStore : ISurveyResponseStore
{
    private readonly ConcurrentDictionary<(TenantId, EntityId), SurveyResponse> _items = new();

    public Task SaveAsync(SurveyResponse response, CancellationToken ct)
    {
        _items[(response.TenantId, response.ResponseId)] = response;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SurveyResponse>> GetByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct)
    {
        IReadOnlyList<SurveyResponse> result = _items.Values
            .Where(r => r.TenantId == tenantId && r.ConversationId == conversationId)
            .ToList();

        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<SurveyResponse>> GetBySurveyAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct)
    {
        IReadOnlyList<SurveyResponse> result = _items.Values
            .Where(r => r.TenantId == tenantId && r.SurveyId == surveyId)
            .ToList();

        return Task.FromResult(result);
    }
}
