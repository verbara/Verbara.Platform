namespace Verbara.Platform.Core.Reports;

public interface IReportRenderer
{
    string ContentType { get; }
    string FileExtension { get; }
    ValueTask<byte[]> RenderAsync(ReportData data, CancellationToken ct);
}

public sealed class ReportData
{
    public required string ReportName { get; init; }
    public required string TenantName { get; init; }
    public required string ReportType { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public IReadOnlyList<ReportDataRow>? Rows { get; init; }
    public IReadOnlyDictionary<string, double>? Summary { get; init; }

    /// <summary>
    /// Optional hex color (e.g. "#1E40AF") for branding table headers in PDF output.
    /// Falls back to the renderer's default when null.
    /// </summary>
    public string? PrimaryColor { get; set; }
}

public sealed record ReportDataRow(
    string Label,
    IReadOnlyDictionary<string, object> Columns);
