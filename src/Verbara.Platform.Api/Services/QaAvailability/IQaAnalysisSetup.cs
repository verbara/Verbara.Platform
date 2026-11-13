namespace Verbara.Platform.Api.Services.QaAvailability;

/// <summary>
/// The configuration half of the QA availability (qa-scores-are-produced-or-not-shown, design D1): which transcript
/// source and speaker mode the host runs with, and what applies to one tenant. Computed from configuration, never from a
/// call; the licence half is asked of the licence guard on each read.
/// </summary>
internal interface IQaAnalysisSetup
{
    /// <summary>The configured transcript source (<c>recording</c>), or null when none is configured.</summary>
    string? TranscriptSource { get; }

    /// <summary>The configured recording mode (<c>two_track</c> or <c>mono</c>), or null when none is configured.</summary>
    string? SpeakerMode { get; }

    /// <summary>What applies to <paramref name="tenantId"/>, resolved over its queues.</summary>
    QaTenantAnalysisSetup ForTenant(string tenantId);
}

/// <summary>
/// What applies to one tenant: whether any of its queues resolves to a scorecard with at least one item, a taxonomy with
/// at least one topic, or a rule set with at least one rule.
/// </summary>
internal readonly record struct QaTenantAnalysisSetup(bool HasScorecard, bool TopicsConfigured, bool ComplianceConfigured)
{
    /// <summary>Nothing applies.</summary>
    public static QaTenantAnalysisSetup None => default;
}

/// <summary>
/// The setup of a host that configures no transcript source: no source, no speaker mode, and nothing applies to any
/// tenant. It is what the host runs with until the transcription settings are bound.
/// </summary>
internal sealed class UnconfiguredQaAnalysisSetup : IQaAnalysisSetup
{
    public static UnconfiguredQaAnalysisSetup Instance { get; } = new();

    private UnconfiguredQaAnalysisSetup()
    {
    }

    public string? TranscriptSource => null;

    public string? SpeakerMode => null;

    public QaTenantAnalysisSetup ForTenant(string tenantId) => QaTenantAnalysisSetup.None;
}
