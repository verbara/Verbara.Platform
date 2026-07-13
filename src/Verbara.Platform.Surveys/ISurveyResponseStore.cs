using Verbara.Platform.Core;

namespace Verbara.Platform.Surveys;

/// <summary>Persistence abstraction for survey responses.</summary>
public interface ISurveyResponseStore
{
    Task SaveAsync(SurveyResponse response, CancellationToken ct);
    Task<IReadOnlyList<SurveyResponse>> GetByConversationAsync(TenantId tenantId, EntityId conversationId, CancellationToken ct);
    Task<IReadOnlyList<SurveyResponse>> GetBySurveyAsync(TenantId tenantId, EntityId surveyId, CancellationToken ct);

    /// <summary>
    /// Returns CSAT-flavored responses for a queue and channel captured within
    /// <paramref name="range"/> (inclusive), ordered by <c>captured_at</c> DESC.
    /// The Postgres implementation is served by the partial indexes on
    /// <c>(tenant_id, queue_name, captured_at DESC) WHERE channel IS NOT NULL</c>
    /// (csat-runner Phase A). Rows whose <c>Channel</c> is null are excluded.
    /// </summary>
    Task<IReadOnlyList<SurveyResponse>> GetByQueueAndChannelAsync(
        TenantId tenantId, string queueName, string channel, DateRange range, CancellationToken ct);

    /// <summary>
    /// Returns CSAT-flavored responses for the whole tenant scope (all queues) captured within
    /// <paramref name="range"/> (inclusive), optionally filtered to a single <paramref name="channel"/>
    /// (null / empty = all channels). Rows whose <c>Channel</c> is null are excluded. Backs the scope-wide
    /// aggregate read (csat-completion, Platform/ADR-0020) — the Postgres analytics implementation reads
    /// this DB-side via <c>GROUP BY queue_name</c>; this store method serves the in-memory parity path.
    /// </summary>
    Task<IReadOnlyList<SurveyResponse>> GetByChannelAndRangeAsync(
        TenantId tenantId, string? channel, DateRange range, CancellationToken ct);
}
