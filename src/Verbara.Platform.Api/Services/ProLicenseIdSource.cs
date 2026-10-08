using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Binds the ledger's licence-id seam to the loaded Pro licence (licensed-agent-metering, design D9): read
/// once per ledger append; null when no licence is loaded, which anchors with the chain's null form.
/// </summary>
internal sealed class ProLicenseIdSource(ILicenseStatus status) : ILicenseIdSource
{
    public string? CurrentLicenseId => status.LicenseId is { Length: > 0 } id ? id : null;
}
