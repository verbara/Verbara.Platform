namespace Verbara.Platform.Queues.Licensing;

/// <summary>A chain's head as the export reports it (<c>chainHeads[]</c>); <see cref="TenantId"/> is null for the deployment chain.</summary>
public sealed record LicenseAgentChainHead(string? TenantId, long HeadSequence, string HeadHash, string? LicenseId);

/// <summary>
/// What the peaks report reads in one consistent snapshot (licensed-agent-reporting): the day zone, every
/// daily row of the range (the reader may return only the latest revision of each chain and day, or all of
/// them), the names of the tenants those rows name, and every chain head.
/// </summary>
public sealed record LicensedAgentPeaksData(
    string DayZone,
    IReadOnlyList<LicenseAgentDaily> Daily,
    IReadOnlyDictionary<string, string> TenantNames,
    IReadOnlyList<LicenseAgentChainHead> ChainHeads);

/// <summary>
/// What the export reads in one consistent snapshot: the day zone, each chain's rows selected by
/// <see cref="LicensedAgentExportSpan"/> (events and daily rows, every revision), and every chain head.
/// </summary>
public sealed record LicensedAgentExportData(
    string DayZone,
    IReadOnlyList<LicenseAgentEvent> Events,
    IReadOnlyList<LicenseAgentDaily> Daily,
    IReadOnlyList<LicenseAgentChainHead> ChainHeads);

/// <summary>
/// The read side of the licensed-agent ledger behind <c>GET /management/licensing/agents</c> and its export
/// (licensed-agent-reporting). Read-only: it never writes a ledger row, a daily row or an audit row.
/// </summary>
public interface ILicensedAgentReportReader
{
    /// <summary>
    /// The daily rows whose <c>day</c> is in <paramref name="firstDay"/>..<paramref name="lastDay"/>, with the chain heads.
    /// The day zone is the one the deployment chain was anchored with, or <paramref name="fallbackDayZone"/>
    /// while it is not anchored.
    /// </summary>
    Task<LicensedAgentPeaksData> ReadPeaksAsync(DateOnly firstDay, DateOnly lastDay, string fallbackDayZone, CancellationToken ct);

    /// <summary>
    /// Every chain's rows for the range as <see cref="LicensedAgentExportSpan"/> selects them, in chain order
    /// (<see cref="LicensedAgentExportSpan.CompareChains"/>) and <c>sequence</c> order within a chain, with
    /// the chain heads, read in one snapshot so the last row of a chain read up to now is its head.
    /// </summary>
    Task<LicensedAgentExportData> ReadExportAsync(DateOnly firstDay, DateOnly lastDay, string fallbackDayZone, CancellationToken ct);
}

/// <summary>
/// Which rows of one chain the export carries for a range (licensed-agent-reporting): a contiguous run of
/// <c>sequence</c> numbers, so an independent verifier can recompute every <c>rowHash</c> and link every
/// <c>prevHash</c> without a gap.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>First row:</b> the row immediately before the first row written at or after the range start
/// (an event's <c>occurredAt</c>, a daily row's <c>closedAt</c>), so verification starts from a known
/// <c>prevHash</c>. A chain with no row since the range start contributes its head row.</item>
/// <item><b>Last row:</b> the last row written before the range end, or the last revision of a day inside
/// the range, whichever comes later in the chain, so every correction of an exported day is included. A range
/// that ends at the present day therefore ends at the chain head.</item>
/// <item>Every row in between is included, whatever it is: events and daily rows share one chain.</item>
/// </list>
/// </remarks>
public static class LicensedAgentExportSpan
{
    /// <summary>The longest range, in days counted inclusively, either report accepts: 15 calendar months fit.</summary>
    public const int MaxRangeDays = 460;

    /// <summary>The UTC instants the range covers: from the start of <paramref name="from"/> to the end of <paramref name="to"/>.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Bounds(DateOnly from, DateOnly to, TimeZoneInfo zone) =>
        (LicenseAgentDayZone.Bounds(from, zone).Start, LicenseAgentDayZone.Bounds(to, zone).End);

    /// <summary>The inclusive first and last <c>sequence</c> of the chain's exported rows, or null when it has none.</summary>
    public static (long First, long Last)? Range(
        IEnumerable<LicenseAgentEvent> events, IEnumerable<LicenseAgentDaily> daily, long headSequence,
        DateTimeOffset start, DateTimeOffset end, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(daily);

        var rows = events.Select(e => (e.Sequence, At: e.OccurredAt, Day: default(DateOnly?)))
            .Concat(daily.Select(d => (d.Sequence, At: d.ClosedAt, Day: new DateOnly?(d.Day))))
            .ToList();

        var firstSince = rows.Where(r => r.At >= start).Select(r => (long?)r.Sequence).Min();
        var lastBefore = rows
            .Where(r => r.At < end || (r.Day is { } day && day >= from && day <= to))
            .Select(r => (long?)r.Sequence)
            .Max();
        return Range(firstSince, lastBefore, headSequence);
    }

    /// <summary>
    /// The span from its two measurements: <paramref name="firstSince"/>, the lowest <c>sequence</c> written at
    /// or after the range start, and <paramref name="lastBefore"/>, the highest written before the range end
    /// or naming a day inside it. A store that measures them in SQL applies the same rule through this method.
    /// </summary>
    public static (long First, long Last)? Range(long? firstSince, long? lastBefore, long headSequence)
    {
        if (lastBefore is not { } last)
            return null;
        var first = Math.Max((firstSince ?? headSequence + 1) - 1, 1);
        return first <= last ? (first, last) : null;
    }

    /// <summary>
    /// Chain order in both reports: tenant chains by tenant id (ordinal), then the deployment chain.
    /// </summary>
    public static int CompareChains(string? leftTenantId, string? rightTenantId) =>
        (leftTenantId, rightTenantId) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(leftTenantId, rightTenantId),
        };
}
