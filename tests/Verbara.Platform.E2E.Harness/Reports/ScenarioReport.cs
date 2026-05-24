using Verbara.Platform.E2E.Harness.Audit;

namespace Verbara.Platform.E2E.Harness.Reports;

/// <summary>
/// Result envelope for one harness scenario run — serialised to JSON
/// under <c>HARNESS_REPORT_DIR</c> and rendered as Markdown for the
/// scenario summary at run end.
/// </summary>
internal sealed record ScenarioReport(
    string ScenarioName,
    string Topology,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Passed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings,
    int PodCount,
    int ClientCount,
    int EventsEmitted,
    int ExpectedReceivesPerClient,
    IReadOnlyList<int> ActualReceivesPerClient,
    int TotalForwarded,
    int TotalSkippedNotLeader,
    IReadOnlyList<string> LeaderPodInstanceIds,
    IReadOnlyDictionary<string, int> PerPodForwarded,
    IReadOnlyDictionary<string, int> PerPodSkippedNotLeader,
    IReadOnlyList<string> AuditBaseUrls);
