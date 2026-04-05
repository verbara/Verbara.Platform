namespace Asterisk.Platform.Core.Reports;

public sealed class ReportExecution
{
    public required string ExecutionId { get; init; }
    public required string ReportId { get; init; }
    public required string TenantId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required string Status { get; init; }
    public required string Format { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? ErrorMessage { get; init; }
    public int? RecipientsSent { get; init; }
}
