using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>Persistence abstraction for survey definitions.</summary>
public interface ISurveyStore
{
    Task<Survey?> GetByIdAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct);
    Task<IReadOnlyList<Survey>> GetActiveAsync(TenantId tenantId, CancellationToken ct);
    Task<IReadOnlyList<Survey>> GetAllAsync(TenantId tenantId, CancellationToken ct);
    Task SaveAsync(Survey survey, CancellationToken ct);
    Task DeleteAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct);
}
