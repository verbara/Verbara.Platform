namespace Verbara.Platform.Core.Reports;

/// <summary>
/// Builds report data for a specific report type.
/// Implementations are registered by type key in the builder registry.
/// </summary>
public interface IReportDataBuilder
{
    string ReportType { get; }

    Task<ReportData> BuildAsync(
        string tenantId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        string? filters,
        CancellationToken ct);
}
