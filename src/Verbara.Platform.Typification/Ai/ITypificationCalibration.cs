using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Determines whether a typification schema has accumulated enough reconciled
/// suggestion data to safely enable AutoFill (<see cref="AiMode.AutoFill"/>) and,
/// at the stricter bar, fully autonomous disposition.
/// </summary>
/// <remarks>
/// Consumers (C1/C4 suggestion endpoint guards, E5 autonomous commit path) call
/// <see cref="GetStatusAsync"/> to decide whether to honour the schema's
/// <see cref="TypificationAiConfig.Mode"/> setting. The service itself does NOT
/// enforce the gate — that is the caller's responsibility.
/// </remarks>
public interface ITypificationCalibration
{
    /// <summary>
    /// Returns the current calibration status for the given schema within
    /// <paramref name="tenantId"/>'s tenant.
    /// </summary>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="schemaId">Schema to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="CalibrationStatus"/> snapshot. If the schema does not exist
    /// all fields default to "not ready".
    /// </returns>
    Task<CalibrationStatus> GetStatusAsync(TenantId tenantId, EntityId schemaId, CancellationToken ct);
}

/// <summary>
/// Snapshot of a schema's empirical calibration state.
/// </summary>
/// <param name="Samples">
/// Number of reconciled suggestions at or above <see cref="TypificationAiConfig.AutoApplyThreshold"/>.
/// </param>
/// <param name="Accuracy">
/// Fraction accepted in the AutoApply confidence band (0 when <see cref="Samples"/> is 0).
/// </param>
/// <param name="AutoFillReady">
/// <see langword="true"/> when the schema passes the minimum-sample and minimum-accuracy
/// thresholds required for <see cref="AiMode.AutoFill"/> to be considered safe.
/// </param>
/// <param name="AutonomousReady">
/// <see langword="true"/> when the schema also passes the stricter accuracy threshold
/// required for fully autonomous disposition (no agent review).
/// </param>
public sealed record CalibrationStatus(
    int Samples,
    double Accuracy,
    bool AutoFillReady,
    bool AutonomousReady);
