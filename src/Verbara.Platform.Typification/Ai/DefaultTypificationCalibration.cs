using Verbara.Platform.Core;
using Verbara.Platform.Typification.Stores;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Default implementation of <see cref="ITypificationCalibration"/>.
/// Registered as a singleton in <c>AddPlatformTypification()</c>.
/// </summary>
internal sealed class DefaultTypificationCalibration : ITypificationCalibration
{
    // ── Calibration gate constants ────────────────────────────────────────────
    // These values may become tenant/schema-level options in a future milestone;
    // for now they are fixed thresholds that apply across all schemas.

    /// <summary>Minimum reconciled-suggestion count before AutoFill is considered safe.</summary>
    private const int MinCalibrationSamples = 200;

    /// <summary>
    /// Minimum accept-rate in the AutoApply confidence band to allow AutoFill.
    /// Approximately "the AI is right ≥85 % of the time above the auto-apply threshold".
    /// </summary>
    private const double MinCalibrationAccuracy = 0.85;

    /// <summary>
    /// Minimum accept-rate in the Autonomous confidence band to allow fully autonomous
    /// disposition without any agent review. Intentionally stricter than
    /// <see cref="MinCalibrationAccuracy"/>.
    /// </summary>
    private const double MinAutonomousAccuracy = 0.95;

    // ── Dependencies ──────────────────────────────────────────────────────────

    private readonly ITypificationSchemaStore _schemaStore;
    private readonly IAiSuggestionStore _suggestionStore;

    public DefaultTypificationCalibration(
        ITypificationSchemaStore schemaStore,
        IAiSuggestionStore suggestionStore)
    {
        _schemaStore = schemaStore;
        _suggestionStore = suggestionStore;
    }

    /// <inheritdoc />
    public async Task<CalibrationStatus> GetStatusAsync(
        TenantId tenantId,
        EntityId schemaId,
        CancellationToken ct)
    {
        // Resolve the schema to read its per-schema confidence thresholds.
        var schema = await _schemaStore.GetByIdAsync(tenantId, schemaId, version: null, ct);
        if (schema is null)
            return new CalibrationStatus(0, 0, false, false);

        // IAiSuggestionStore uses EntityId for tenant (consistent with the B2 SaveAsync path).
        // Mirror the conversion from DefaultTypificationProvenanceService (B3).
        var tenantEntityId = EntityId.From(tenantId.Value);

        // ── AutoFill band ─────────────────────────────────────────────────────
        var (samples, acceptRate) = await _suggestionStore.QueryAccuracyAsync(
            tenantEntityId, schemaId, schema.AiConfig.AutoApplyThreshold, ct);

        var autoFillReady =
            samples >= MinCalibrationSamples &&
            acceptRate >= MinCalibrationAccuracy;

        // ── Autonomous band ───────────────────────────────────────────────────
        var (autoSamples, autoAcceptRate) = await _suggestionStore.QueryAccuracyAsync(
            tenantEntityId, schemaId, schema.AiConfig.AutonomousThreshold, ct);

        var autonomousReady =
            autoSamples >= MinCalibrationSamples &&
            autoAcceptRate >= MinAutonomousAccuracy;

        // Report the AutoApply-band headline figures (samples / accuracy).
        return new CalibrationStatus(samples, acceptRate, autoFillReady, autonomousReady);
    }
}
