using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>
/// A per-queue CSAT aggregate row within a scope-wide roll-up (csat-completion, Platform/ADR-0020).
/// Mirrors the <c>CsatResponseDto</c> projection the read endpoint returns — one row per queue in the
/// requested range/channel.
/// </summary>
/// <param name="QueueName">The queue the aggregate covers.</param>
/// <param name="TotalResponses">Number of CSAT responses for the queue in the range.</param>
/// <param name="AverageRating">Mean CSAT rating (1..5) for the queue; 0 when none.</param>
public sealed record CsatQueueAggregate(string QueueName, int TotalResponses, double AverageRating);

/// <summary>
/// A tenant/scope-wide CSAT roll-up (csat-completion): the top-level totals plus one
/// <see cref="CsatQueueAggregate"/> per queue. Backs <c>GET /api/v1/analytics/csat</c>.
/// </summary>
/// <param name="TotalResponses">Sum of responses across all queues in the scope.</param>
/// <param name="AverageRating">Response-weighted mean rating across the scope; 0 when none.</param>
/// <param name="Queues">The per-queue rows contributing to the scope totals.</param>
public sealed record CsatScopeAggregate(
    int TotalResponses,
    double AverageRating,
    IReadOnlyList<CsatQueueAggregate> Queues);

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
    /// Scope-wide CSAT roll-up across every queue in the tenant for a captured-at range, optionally
    /// filtered to a single <paramref name="channel"/> (null / empty = all channels). Returns the
    /// top-level totals plus one <see cref="CsatQueueAggregate"/> per queue (<c>GROUP BY queue_name</c>),
    /// backed by the same <c>(tenant_id, queue_name, captured_at DESC) WHERE channel IS NOT NULL</c>
    /// partial index as <see cref="GetByQueueAndChannelAsync"/> — no schema change (csat-completion,
    /// Platform/ADR-0020). Powers <c>GET /api/v1/analytics/csat</c>.
    /// </summary>
    Task<CsatScopeAggregate> GetScopeAggregateAsync(
        TenantId tenantId, string? channel, DateRange range, CancellationToken ct);

    /// <summary>
    /// Per-queue aggregates keyed off a <c>__queue</c> answer-marker hack.
    /// Superseded by <see cref="GetByQueueAndChannelAsync"/>, which reads the
    /// indexed <c>queue_name</c> column directly. Removed one minor release later
    /// (v2.19.0) per the 2-release deprecation cadence (Pro/ADR-0012).
    /// </summary>
    [Obsolete("Use GetByQueueAndChannelAsync; removed in v2.19.0 (csat-runner Phase A / Pro ADR-0012 cadence).")]
    Task<SurveyScoreSummary> GetByQueueAsync(TenantId tenantId, EntityId surveyId, string queueName, CancellationToken ct);
}
