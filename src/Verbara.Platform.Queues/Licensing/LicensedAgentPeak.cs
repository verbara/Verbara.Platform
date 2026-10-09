namespace Verbara.Platform.Queues.Licensing;

/// <summary>
/// The daily simultaneous peak of licensed agents, computed from one chain's ledger rows
/// (licensed-agent-daily-close, design D7). Pure: the close, the in-memory twin and an offline verifier
/// compute the same figure from the same rows.
/// </summary>
/// <remarks>
/// The count at an instant is the set of agents whose latest row (in <c>sequence</c> order) says
/// <c>counted</c>. A takeover, transfer or reassign adds its receiver when it is counted and not already in
/// the set; it never removes anyone. Every other agent row sets the agent's state to its <c>counted</c>.
/// Chain rows (<c>chain_anchored</c>, <c>chain_reanchored</c>) carry no agent and change nothing.
/// </remarks>
public static class LicensedAgentPeak
{
    /// <summary>
    /// The maximum size the counted set reaches from <paramref name="startSet"/> (the agents counted at the
    /// start of the day) while <paramref name="eventsOfDay"/> are applied in <c>sequence</c> order.
    /// </summary>
    public static int Compute(IReadOnlySet<string> startSet, IEnumerable<LicenseAgentEvent> eventsOfDay)
    {
        ArgumentNullException.ThrowIfNull(startSet);
        ArgumentNullException.ThrowIfNull(eventsOfDay);

        var counted = new HashSet<string>(startSet, StringComparer.Ordinal);
        var peak = counted.Count;
        foreach (var row in eventsOfDay.OrderBy(r => r.Sequence))
        {
            Apply(counted, row);
            if (counted.Count > peak)
                peak = counted.Count;
        }

        return peak;
    }

    /// <summary>The agents counted after applying <paramref name="rowsBefore"/> in <c>sequence</c> order.</summary>
    public static IReadOnlySet<string> StartSet(IEnumerable<LicenseAgentEvent> rowsBefore)
    {
        ArgumentNullException.ThrowIfNull(rowsBefore);

        var counted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rowsBefore.OrderBy(r => r.Sequence))
            Apply(counted, row);
        return counted;
    }

    /// <summary>
    /// The peak of <paramref name="day"/> in <paramref name="zone"/>: the start set from every row that
    /// occurred before the day starts, then the rows that occurred inside it.
    /// </summary>
    public static int ComputeDay(IEnumerable<LicenseAgentEvent> chainRows, DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(chainRows);
        var (start, end) = LicenseAgentDayZone.Bounds(day, zone);
        var rows = chainRows as IReadOnlyCollection<LicenseAgentEvent> ?? chainRows.ToList();
        return Compute(
            StartSet(rows.Where(r => r.OccurredAt < start)),
            rows.Where(r => r.OccurredAt >= start && r.OccurredAt < end));
    }

    private static void Apply(HashSet<string> counted, LicenseAgentEvent row)
    {
        if (row.AgentId is not { } agentId || row.Counted is not { } isCounted)
            return;

        if (LicenseAgentEventKinds.IsOwnership(row.Kind))
        {
            if (isCounted)
                counted.Add(agentId);
            return;
        }

        if (isCounted)
            counted.Add(agentId);
        else
            counted.Remove(agentId);
    }
}
