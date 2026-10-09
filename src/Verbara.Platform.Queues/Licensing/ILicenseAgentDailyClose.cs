namespace Verbara.Platform.Queues.Licensing;

/// <summary>What one close pass did.</summary>
public sealed record LicenseAgentCloseResult(int ChainsAnchored, int DaysClosed, int Corrections, int ChainsFailed);

/// <summary>
/// The daily close of the licensed-agent ledger (licensed-agent-daily-close, design D7): it turns the ledger
/// into one <c>license_agent_daily</c> row per Customer tenant and ended day, plus the deployment total.
/// Correctness never depends on leadership: each chain is closed under its head lock, so a second replica
/// finds the rows written and writes nothing.
/// </summary>
public interface ILicenseAgentDailyClose
{
    /// <summary>
    /// The first-tick step: refuses a day zone that differs from the one the deployment chain was anchored
    /// with (<see cref="LicenseAgentDayZoneMismatchException"/>, nothing written), then anchors the
    /// deployment chain (recording <paramref name="dayZone"/>) and every Customer tenant chain without a head.
    /// Returns the number of chains it anchored.
    /// </summary>
    Task<int> PrepareAsync(string dayZone, CancellationToken ct);

    /// <summary>
    /// Anchors Customer chains still without a head, closes every ended day of every Customer tenant (in every
    /// tenant status, whatever its parent) and then of the deployment chain, and appends the corrections late
    /// rows call for. <paramref name="now"/> decides which days have ended.
    /// </summary>
    Task<LicenseAgentCloseResult> CloseAsync(TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// The fixed 15-month retention of the ledger and its daily rows (design D8): for every chain, whatever the
/// tenant's retention policy, rows older than the window are deleted and a <c>purge_log</c> row with
/// <c>subject_type</c> <c>license_agent</c> records the counts and the last purged sequence and hash.
/// </summary>
public interface ILicenseAgentRetentionPurge
{
    /// <summary>The window, a constant: no setting, policy or dry-run flag changes it.</summary>
    public const int RetentionMonths = 15;

    /// <summary>Purges every chain; returns the number of chains it purged rows from.</summary>
    Task<int> PurgeAsync(TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// The configured day zone differs from the one the deployment chain was anchored with. The day zone is
/// fixed once per deployment (Q5): the close stops rather than cut days differently from the closed ones.
/// </summary>
public sealed class LicenseAgentDayZoneMismatchException : Exception
{
    public LicenseAgentDayZoneMismatchException(string anchoredZone, string configuredZone)
        : base($"The licensed-agent day zone is fixed at '{anchoredZone}' (recorded when the deployment chain was " +
               $"anchored), but {LicenseAgentDayZone.ConfigurationKey} is '{configuredZone}'. The day close is stopped " +
               $"and writes nothing. Set {LicenseAgentDayZone.ConfigurationKey} back to '{anchoredZone}' and restart.")
    {
        AnchoredZone = anchoredZone;
        ConfiguredZone = configuredZone;
    }

    public string AnchoredZone { get; }
    public string ConfiguredZone { get; }
}
