using Verbara.Platform.Conversations;
using Verbara.Platform.Typification.Resolution;
using Verbara.Platform.Typification.Stores;
using Verbara.Sdk.Pro.Licensing;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Default implementation of <see cref="IAutonomousDispositionPolicy"/>. Registered as a singleton
/// in <c>AddPlatformTypification()</c>, mirroring <see cref="DefaultTypificationCalibration"/>.
/// </summary>
/// <remarks>
/// <para>
/// Gate-evaluation order (first failing gate wins, returning its specific
/// <see cref="AutonomousSkipReason"/>):
/// </para>
/// <list type="number">
///   <item><description>activation gate present and not revoked → else <see cref="AutonomousSkipReason.GateDisabled"/></description></item>
///   <item><description>deployment holds both <see cref="LicenseFeature.AdvancedTypification"/> AND <see cref="LicenseFeature.TypificationAi"/> → else <see cref="AutonomousSkipReason.LicenseMissing"/></description></item>
///   <item><description>a pending AI suggestion exists → else <see cref="AutonomousSkipReason.NoSuggestion"/></description></item>
///   <item><description>confidence ≥ schema <see cref="TypificationAiConfig.AutonomousThreshold"/> → else <see cref="AutonomousSkipReason.BelowThreshold"/></description></item>
///   <item><description>conversation still in <see cref="ConversationState.WrapUp"/> → else <see cref="AutonomousSkipReason.NotWrapUp"/></description></item>
///   <item><description>suggested leaf still a valid leaf in the active published schema → else <see cref="AutonomousSkipReason.SchemaMutation"/></description></item>
/// </list>
/// <para>
/// The license check reuses <see cref="ILicenseStatus"/> — the exact abstraction
/// <c>LicenseGateMiddleware</c> reads on the HTTP path (the Pro license is per-<i>deployment</i>,
/// not per-tenant, so the same process-wide snapshot governs both paths). The confidence threshold
/// and leaf validity both come from the conversation's <i>effective</i> resolved schema
/// (<see cref="ITypificationResolver"/>), i.e. the same published-schema resolution the manual
/// /typify path uses — so a binding override or a cosmetic republish is honoured identically.
/// </para>
/// </remarks>
internal sealed class DefaultAutonomousDispositionPolicy : IAutonomousDispositionPolicy
{
    /// <summary>Both Pro entitlements required for autonomous stamping (mirrors ConversationEndpoints.cs:48).</summary>
    private const LicenseFeature RequiredFeatures =
        LicenseFeature.AdvancedTypification | LicenseFeature.TypificationAi;

    private readonly ITenantAutonomousDispositionStore _gateStore;
    private readonly ILicenseStatus _licenseStatus;
    private readonly ITypificationResolver _resolver;

    public DefaultAutonomousDispositionPolicy(
        ITenantAutonomousDispositionStore gateStore,
        ILicenseStatus licenseStatus,
        ITypificationResolver resolver)
    {
        _gateStore = gateStore;
        _licenseStatus = licenseStatus;
        _resolver = resolver;
    }

    /// <inheritdoc />
    public async Task<AutonomousDispositionDecision> EvaluateAsync(
        Conversation conversation,
        AiSuggestionRecord? suggestion,
        CancellationToken ct)
    {
        // 1. Activation gate — present and not revoked (controller instruction, ADR-0034 Decision 3).
        var gate = await _gateStore.GetAsync(conversation.TenantId, ct);
        if (gate is not { IsActive: true })
            return AutonomousDispositionDecision.Skip(AutonomousSkipReason.GateDisabled);

        // 2. License — deployment must hold BOTH Pro entitlements (same ILicenseStatus the
        //    LicenseGateMiddleware reads). LicenseFeature is the Pro [Flags] enum, NOT Core PlanFeature.
        if (!_licenseStatus.LicensedFeatures.HasFlag(RequiredFeatures))
            return AutonomousDispositionDecision.Skip(AutonomousSkipReason.LicenseMissing);

        // 3. Pending AI suggestion must exist.
        if (suggestion is null)
            return AutonomousDispositionDecision.Skip(AutonomousSkipReason.NoSuggestion);

        // Resolve the schema that actually governs this conversation (effective AiConfig + nodes).
        // Threshold (step 4) and leaf validity (step 6) both read from this single resolution.
        var resolved = await _resolver.ResolveForConversationAsync(conversation, ct);

        // 4. Confidence ≥ the effective schema's AutonomousThreshold. When no schema resolves there
        //    is no threshold to clear AND no taxonomy to validate against — treat as a schema
        //    mutation (the binding/schema is gone) rather than a confidence question.
        if (resolved is null)
            return AutonomousDispositionDecision.Skip(AutonomousSkipReason.SchemaMutation);

        if (suggestion.Confidence < resolved.EffectiveAiConfig.AutonomousThreshold)
            return AutonomousDispositionDecision.Skip(AutonomousSkipReason.BelowThreshold);

        // 5. Conversation still in WrapUp (read-side check; the worker still does the CAS write).
        if (conversation.State != ConversationState.WrapUp)
            return AutonomousDispositionDecision.Skip(AutonomousSkipReason.NotWrapUp);

        // 6. The suggested leaf must still be a valid leaf in the active schema (mirrors the manual
        //    /typify leaf lookup: schema.Nodes.FirstOrDefault(n => n.NodeId == leaf)?.IsLeaf).
        var node = resolved.Schema.Nodes.FirstOrDefault(n => n.NodeId == suggestion.SuggestedLeafNodeId);
        if (node is not { IsLeaf: true })
            return AutonomousDispositionDecision.Skip(AutonomousSkipReason.SchemaMutation);

        return AutonomousDispositionDecision.Commit(
            suggestion.SuggestedLeafNodeId,
            suggestion.SuggestedNodePath,
            suggestion.Confidence);
    }
}
