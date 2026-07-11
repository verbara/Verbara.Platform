using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>Analytics queries over collected survey responses.</summary>
public interface ISurveyAnalytics
{
    Task<SurveyScoreSummary> GetSummaryAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct);
    Task<SurveyScoreSummary> GetByAgentAsync(TenantId tenantId, EntityId surveyId, EntityId agentId, CancellationToken ct);

    /// <summary>
    /// Per-queue CSAT aggregates filtered by channel and a captured-at range,
    /// backed by the <c>survey_responses</c> partial indexes on
    /// <c>(tenant_id, queue_name, captured_at DESC) WHERE channel IS NOT NULL</c>
    /// (csat-runner Phase A). Replaces the obsolete <see cref="GetByQueueAsync"/>.
    /// </summary>
    Task<SurveyScoreSummary> GetByQueueAndChannelAsync(
        TenantId tenantId, string queueName, string channel, DateRange range, CancellationToken ct);

    /// <summary>
    /// Per-queue aggregates keyed off a <c>__queue</c> answer-marker hack.
    /// Superseded by <see cref="GetByQueueAndChannelAsync"/>, which reads the
    /// indexed <c>queue_name</c> column directly. Removed one minor release later
    /// (v2.19.0) per the 2-release deprecation cadence (Pro/ADR-0012).
    /// </summary>
    [Obsolete("Use GetByQueueAndChannelAsync; removed in v2.19.0 (csat-runner Phase A / Pro ADR-0012 cadence).")]
    Task<SurveyScoreSummary> GetByQueueAsync(TenantId tenantId, EntityId surveyId, string queueName, CancellationToken ct);
}
