namespace Verbara.Platform.Core.Reports;

public sealed class ScheduledReport
{
    public required string ReportId { get; init; }
    public required string TenantId { get; init; }
    public required string Name { get; init; }
    public required string ReportType { get; init; }
    public required string Schedule { get; init; }
    public string? Filters { get; init; }
    public required string Recipients { get; init; }
    public required string Format { get; init; }
    public bool IsActive { get; init; } = true;
    public required string CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public DateTimeOffset? NextRunAt { get; init; }
}
