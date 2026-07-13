namespace Verbara.Platform.Api.Dtos;

/// <summary>
/// Scope-wide CSAT roll-up returned by <c>GET /api/v1/analytics/csat</c> (csat-completion,
/// Platform/ADR-0020). The envelope carries the tenant/scope totals; each <see cref="Queues"/> row
/// reuses the existing <see cref="CsatResponseDto"/> projection verbatim (one row per queue), frozen by
/// <c>fixtures/csat-aggregate-analytics.v1.json</c> (verbatim-fixture-citation rule).
/// </summary>
/// <param name="TotalResponses">Sum of CSAT responses across every queue in the scope/range.</param>
/// <param name="AverageRating">Response-weighted mean rating (1..5) across the scope; 0 when none.</param>
/// <param name="RangeStart">Inclusive start of the captured-at range.</param>
/// <param name="RangeEnd">Inclusive end of the captured-at range.</param>
/// <param name="Queues">One <see cref="CsatResponseDto"/> row per queue contributing to the totals.</param>
internal sealed record CsatAggregateDto(
    int TotalResponses,
    double AverageRating,
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd,
    IReadOnlyList<CsatResponseDto> Queues);
