using Verbara.Platform.Core.Reports;
using Verbara.Sdk.Pro.Analytics;

namespace Verbara.Platform.Api.Services.Reports;

internal sealed class QueueAnalyticsReportBuilder : IReportDataBuilder
{
    private readonly IIntervalSnapshotStore _snapshotStore;

    public QueueAnalyticsReportBuilder(IIntervalSnapshotStore snapshotStore)
    {
        _snapshotStore = snapshotStore;
    }

    public string ReportType => "queue_analytics";

    public async Task<ReportData> BuildAsync(
        string tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd,
        string? filters, CancellationToken ct)
    {
        var snapshots = await _snapshotStore.QueryAsync(tenantId, periodStart, periodEnd, ct: ct);

        // Group by queue and aggregate
        var byQueue = snapshots.GroupBy(s => s.QueueName);

        var rows = new List<ReportDataRow>();
        int totalOffered = 0, totalAnswered = 0, totalAbandoned = 0, totalSlaMet = 0;
        long totalWaitMs = 0, totalHandleMs = 0;

        foreach (var group in byQueue)
        {
            var queue = group.Key;
            var offered = group.Sum(s => s.CallsOffered);
            var answered = group.Sum(s => s.CallsAnswered);
            var abandoned = group.Sum(s => s.CallsAbandoned);
            var shortAbandons = group.Sum(s => s.ShortAbandons);
            var slaMet = group.Sum(s => s.SlaMetCount);
            var denominator = offered - shortAbandons;
            var slaPercent = denominator > 0 ? slaMet * 100.0 / denominator : 0.0;
            var asaMs = answered > 0 ? (double)group.Sum(s => s.TotalWaitMs) / answered : 0.0;
            var ahtMs = answered > 0
                ? (double)group.Sum(s => s.TotalTalkMs + s.TotalHoldMs + s.TotalAcwMs) / answered
                : 0.0;

            rows.Add(new ReportDataRow(queue, new Dictionary<string, object>
            {
                ["Offered"] = offered,
                ["Answered"] = answered,
                ["Abandoned"] = abandoned,
                ["SLA %"] = Math.Round(slaPercent, 1),
                ["Avg Speed of Answer (s)"] = Math.Round(asaMs / 1000.0, 1),
                ["Avg Handle Time (s)"] = Math.Round(ahtMs / 1000.0, 1),
            }));

            totalOffered += offered;
            totalAnswered += answered;
            totalAbandoned += abandoned;
            totalSlaMet += slaMet;
            totalWaitMs += group.Sum(s => s.TotalWaitMs);
            totalHandleMs += group.Sum(s => s.TotalTalkMs + s.TotalHoldMs + s.TotalAcwMs);
        }

        var summary = new Dictionary<string, double>
        {
            ["Total Offered"] = totalOffered,
            ["Total Answered"] = totalAnswered,
            ["Total Abandoned"] = totalAbandoned,
            ["Weighted SLA %"] = totalOffered > 0 ? Math.Round(totalSlaMet * 100.0 / totalOffered, 1) : 0,
            ["Avg Speed of Answer (s)"] = totalAnswered > 0 ? Math.Round((double)totalWaitMs / totalAnswered / 1000.0, 1) : 0,
            ["Avg Handle Time (s)"] = totalAnswered > 0 ? Math.Round((double)totalHandleMs / totalAnswered / 1000.0, 1) : 0,
        };

        return new ReportData
        {
            ReportName = "Queue Analytics",
            TenantName = tenantId,
            ReportType = ReportType,
            From = periodStart,
            To = periodEnd,
            GeneratedAt = DateTimeOffset.UtcNow,
            Rows = rows,
            Summary = summary,
        };
    }
}
