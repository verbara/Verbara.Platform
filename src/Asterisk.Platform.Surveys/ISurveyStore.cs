using Asterisk.Platform.Core;

namespace Asterisk.Platform.Surveys;

/// <summary>Persistence abstraction for survey definitions.</summary>
public interface ISurveyStore
{
    Task<Survey?> GetByIdAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct);
    Task<IReadOnlyList<Survey>> GetActiveAsync(TenantId tenantId, CancellationToken ct);
    Task SaveAsync(Survey survey, CancellationToken ct);
}
