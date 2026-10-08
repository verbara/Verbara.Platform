namespace Verbara.Platform.Queues.Licensing;

/// <summary>
/// The id of the licence loaded now, read once per ledger append (licensed-agent-metering, design D9). Queues
/// does not reference the Pro licensing package: the host binds this seam to <c>ILicenseStatus.LicenseId</c>.
/// </summary>
public interface ILicenseIdSource
{
    /// <summary>The loaded licence's id, or null when no licence is loaded (the chain's null form).</summary>
    string? CurrentLicenseId { get; }
}

/// <summary>A fixed licence id: the source for hosts and tests without a licence service.</summary>
public sealed class FixedLicenseIdSource(string? licenseId) : ILicenseIdSource
{
    public string? CurrentLicenseId { get; set; } = licenseId;
}
