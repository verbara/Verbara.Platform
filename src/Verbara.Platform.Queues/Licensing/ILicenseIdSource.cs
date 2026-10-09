namespace Verbara.Platform.Queues.Licensing;

/// <summary>
/// The id of the licence loaded now, read once per ledger append (licensed-agent-metering, design D9). Queues
/// does not reference the Pro licensing package: the host binds this seam to <c>ILicenseStatus.LicenseId</c>.
/// </summary>
public interface ILicenseIdSource
{
    /// <summary>The loaded licence's id, or null when no licence is loaded (the chain's null form).</summary>
    string? CurrentLicenseId { get; }

    /// <summary>
    /// Whether the host's licence load attempt has completed, whatever its outcome (valid, expired, invalid or
    /// no licence file). The daily-close worker anchors nothing until it has, so a deployment chain is never
    /// anchored with the null form a moment before the licence loads (a spurious <c>chain_reanchored</c> would
    /// follow). It never waits for a <em>valid</em> licence: a deployment without one meters with the null form.
    /// </summary>
    bool LoadCompleted { get; }
}

/// <summary>A fixed licence id: the source for hosts and tests without a licence service.</summary>
public sealed class FixedLicenseIdSource(string? licenseId) : ILicenseIdSource
{
    public string? CurrentLicenseId { get; set; } = licenseId;

    /// <summary>Always true: a fixed id has nothing left to load.</summary>
    public bool LoadCompleted => true;
}
