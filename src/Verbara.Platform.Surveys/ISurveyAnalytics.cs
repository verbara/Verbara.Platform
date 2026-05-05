using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>Analytics queries over collected survey responses.</summary>
public interface ISurveyAnalytics
{
    Task<SurveyScoreSummary> GetSummaryAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct);
    Task<SurveyScoreSummary> GetByAgentAsync(TenantId tenantId, EntityId surveyId, EntityId agentId, CancellationToken ct);
    Task<SurveyScoreSummary> GetByQueueAsync(TenantId tenantId, EntityId surveyId, string queueName, CancellationToken ct);
}
