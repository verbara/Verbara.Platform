using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>Persistence abstraction for survey responses.</summary>
public interface ISurveyResponseStore
{
    Task SaveAsync(SurveyResponse response, CancellationToken ct);
    Task<IReadOnlyList<SurveyResponse>> GetByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
    Task<IReadOnlyList<SurveyResponse>> GetBySurveyAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct);
}
