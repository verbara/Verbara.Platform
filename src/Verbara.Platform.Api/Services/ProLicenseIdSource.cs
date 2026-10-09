using Verbara.Platform.Queues.Licensing;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Binds the ledger's licence-id seam to the loaded Pro licence (licensed-agent-metering, design D9): read
/// once per ledger append; null when no licence is loaded, which anchors with the chain's null form.
/// The load attempt has completed once the tracker has recorded a validation: Pro's
/// <c>LicenseValidationHostedService.StartAsync</c> updates it on every path, including no licence file and an
/// unreadable one, and nothing else leaves <see cref="ILicenseStatus.LastValidatedAt"/> at its default.
/// </summary>
internal sealed class ProLicenseIdSource(ILicenseStatus status) : ILicenseIdSource
{
    public string? CurrentLicenseId => status.LicenseId is { Length: > 0 } id ? id : null;

    public bool LoadCompleted => status.LastValidatedAt != default;
}
