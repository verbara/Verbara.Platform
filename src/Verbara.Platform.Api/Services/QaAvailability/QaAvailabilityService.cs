using Verbara.Platform.Api.Endpoints;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Api.Services.QaAvailability;

/// <summary>
/// Computes the QA availability of one tenant (qa-scores-are-produced-or-not-shown, design D1). <c>reason</c> is the
/// first missing piece, in this order: the analysis is registered on this host, the licence grants call analytics, a
/// transcript source is configured, and a scorecard with at least one item applies to the tenant. Whether the analysis
/// is registered and the setup are fixed at startup; the licence is asked of <see cref="ILicenseGuard"/> on every read,
/// because it can lapse or be renewed while the process runs.
/// </summary>
internal sealed class QaAvailabilityService
{
    private readonly bool _analysisRegistered;
    private readonly IQaAnalysisSetup _setup;
    private readonly ILicenseGuard _licenseGuard;

    public QaAvailabilityService(bool analysisRegistered, IQaAnalysisSetup setup, ILicenseGuard licenseGuard)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(licenseGuard);
        _analysisRegistered = analysisRegistered;
        _setup = setup;
        _licenseGuard = licenseGuard;
    }

    public async ValueTask<QaAvailabilityDto> GetAsync(string tenantId, CancellationToken ct)
    {
        var transcriptSource = _setup.TranscriptSource;

        if (!_analysisRegistered)
            return NotRunning(QaAvailabilityDto.ReasonAnalysisNotRegistered, transcriptSource);

        var licence = await _licenseGuard.CanExecuteAsync(LicenseFeature.CallAnalytics, ct).ConfigureAwait(false);
        if (!licence.Allowed)
            return NotRunning(QaAvailabilityDto.ReasonNotLicensed, transcriptSource);

        if (transcriptSource is null)
            return NotRunning(QaAvailabilityDto.ReasonNoTranscriptSource, transcriptSource);

        var tenant = _setup.ForTenant(tenantId);

        // The backlog counter is filled once the analysis queue records it; until then it stays null.
        return new QaAvailabilityDto(
            Available: tenant.HasScorecard,
            AnalysisRunning: true,
            Reason: tenant.HasScorecard ? null : QaAvailabilityDto.ReasonNoScorecard,
            TranscriptSource: transcriptSource,
            SpeakerMode: _setup.SpeakerMode,
            TopicsConfigured: tenant.TopicsConfigured,
            ComplianceConfigured: tenant.ComplianceConfigured,
            SentimentSupported: tenant.SentimentSupported,
            Backlog: null);
    }

    private static QaAvailabilityDto NotRunning(string reason, string? transcriptSource) =>
        new(false, false, reason, transcriptSource, null, null, null, null, null);
}
