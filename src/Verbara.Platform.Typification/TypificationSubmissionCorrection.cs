using Verbara.Platform.Core;

namespace Verbara.Platform.Typification;

/// <summary>
/// A SEPARATE, append-only correction of an autonomously stamped disposition (ADR-0034 Decision 5).
/// The original <see cref="TypificationSubmission"/> (its leaf, node path, confidence, and
/// <see cref="SubmissionSource.AutoAi"/>) stays IMMUTABLE — a supervisor's human-chosen disposition
/// is recorded here, referencing the corrected conversation. The submission's
/// <see cref="TypificationSubmission.CorrectionState"/> is flipped to
/// <see cref="CorrectionState.Corrected"/> as a status pointer only; the conversation is NOT reopened.
/// One row per conversation (primary key <c>(tenant_id, conversation_id)</c>) in
/// <c>typification_submission_corrections</c> — a conversation is correctable at most once.
/// </summary>
public sealed class TypificationSubmissionCorrection : ITenantScoped
{
    /// <summary>Tenant that owns the corrected conversation (primary key part).</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The conversation whose autonomous disposition this record corrects (primary key part).</summary>
    public required EntityId ConversationId { get; init; }

    /// <summary>The human-chosen leaf node id (last element of <see cref="CorrectedNodePath"/>).</summary>
    public required EntityId CorrectedLeafNodeId { get; init; }

    /// <summary>The human-chosen root→leaf node-id path.</summary>
    public required IReadOnlyList<EntityId> CorrectedNodePath { get; init; }

    /// <summary>User id of the supervisor who recorded the correction.</summary>
    public required string CorrectedByUserId { get; init; }

    /// <summary>UTC timestamp when the correction was recorded.</summary>
    public required DateTimeOffset CorrectedAt { get; init; }
}
