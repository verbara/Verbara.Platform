using Asterisk.Platform.Core.Reports;
using Asterisk.Sdk.Pro.Analytics;

namespace Asterisk.Platform.Api.Services.Reports;

internal sealed class AgentPerformanceReportBuilder : IReportDataBuilder
{
    private readonly IIntervalSnapshotStore _snapshotStore;

    public AgentPerformanceReportBuilder(IIntervalSnapshotStore snapshotStore)
    {
        _snapshotStore = snapshotStore;
    }

    public string ReportType => "agent_performance";

    public async Task<ReportData> BuildAsync(
        string tenantId, DateTimeOffset periodStart, DateTimeOffset periodEnd,
        string? filters, CancellationToken ct)
    {
        var snapshots = await _snapshotStore.QueryAgentAsync(tenantId, periodStart, periodEnd, ct: ct);

        // Group by agent and aggregate
        var byAgent = snapshots.GroupBy(s => s.AgentId);

        var rows = new List<ReportDataRow>();
        int totalHandled = 0, totalRna = 0, totalTransfers = 0;
        long totalPauseMs = 0;
        double totalAhtMs = 0;

        foreach (var group in byAgent)
        {
            var agentId = group.Key;
            var handled = group.Sum(s => s.CallsHandled);
            var ahtMs = handled > 0
                ? (double)group.Sum(s => s.TotalTalkMs + s.TotalHoldMs + s.TotalAcwMs) / handled
                : 0.0;
            var occupancy = group.Sum(s => s.LoginDurationMs) > 0
                ? (double)group.Sum(s => s.TotalTalkMs + s.TotalHoldMs + s.TotalAcwMs) /
                  group.Sum(s => s.LoginDurationMs) * 100.0
                : 0.0;
            var pauseMinutes = group.Sum(s => s.TotalPauseMs) / 60_000.0;
            var transfers = group.Sum(s => s.Transfers);
            var rna = group.Sum(s => s.RnaCount);

            rows.Add(new ReportDataRow(agentId, new Dictionary<string, object>
            {
                ["Calls Handled"] = handled,
                ["Avg Handle Time (s)"] = Math.Round(ahtMs / 1000.0, 1),
                ["Occupancy %"] = Math.Round(occupancy, 1),
                ["Pause Time (min)"] = Math.Round(pauseMinutes, 1),
                ["Transfers"] = transfers,
                ["RNA Count"] = rna,
            }));

            totalHandled += handled;
            totalAhtMs += ahtMs * handled;
            totalPauseMs += group.Sum(s => s.TotalPauseMs);
            totalTransfers += transfers;
            totalRna += rna;
        }

        var summary = new Dictionary<string, double>
        {
            ["Total Calls Handled"] = totalHandled,
            ["Avg Handle Time (s)"] = totalHandled > 0 ? Math.Round(totalAhtMs / totalHandled / 1000.0, 1) : 0,
            ["Total Pause Time (min)"] = Math.Round(totalPauseMs / 60_000.0, 1),
            ["Total Transfers"] = totalTransfers,
            ["Total RNA"] = totalRna,
        };

        return new ReportData
        {
            ReportName = "Agent Performance",
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
