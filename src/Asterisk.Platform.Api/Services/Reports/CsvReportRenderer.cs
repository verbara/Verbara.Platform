using System.Text;
using Asterisk.Platform.Core.Reports;

namespace Asterisk.Platform.Api.Services.Reports;

internal sealed class CsvReportRenderer : IReportRenderer
{
    public string ContentType => "text/csv";
    public string FileExtension => "csv";

    public ValueTask<byte[]> RenderAsync(ReportData data, CancellationToken ct)
    {
        var sb = new StringBuilder();

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        // Header comment block with report metadata
        sb.AppendLine(ci, $"# Report: {data.ReportName}");
        sb.AppendLine(ci, $"# Tenant: {data.TenantName}");
        sb.AppendLine(ci, $"# Type: {data.ReportType}");
        sb.AppendLine(ci, $"# Period: {data.From:yyyy-MM-dd HH:mm:ss zzz} — {data.To:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine(ci, $"# Generated: {data.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();

        if (data.Rows is { Count: > 0 })
        {
            // Column headers derived from first row
            var headers = data.Rows[0].Columns.Keys.ToArray();
            sb.Append("Label");
            foreach (var h in headers)
            {
                sb.Append(',');
                sb.Append(Escape(h));
            }
            sb.AppendLine();

            // Data rows
            foreach (var row in data.Rows)
            {
                sb.Append(Escape(row.Label));
                foreach (var h in headers)
                {
                    sb.Append(',');
                    if (row.Columns.TryGetValue(h, out var val))
                        sb.Append(Escape(val?.ToString() ?? string.Empty));
                }
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("# No data available for this report period.");
        }

        // Summary section
        if (data.Summary is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("# Summary");
            sb.AppendLine("Metric,Value");
            foreach (var kv in data.Summary)
            {
                sb.Append(Escape(kv.Key));
                sb.Append(',');
                sb.AppendLine(kv.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        // UTF-8 BOM + content
        var bom = Encoding.UTF8.GetPreamble();
        var content = Encoding.UTF8.GetBytes(sb.ToString());
        var result = new byte[bom.Length + content.Length];
        bom.CopyTo(result, 0);
        content.CopyTo(result, bom.Length);

        return ValueTask.FromResult(result);
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
