using Verbara.Platform.Conversations;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Pure commit-or-skip decision for autonomous AI disposition stamping on the existing
/// abandoned-wrap-up auto-close (ADR-0034 Decision 2/6). Given a timed-out conversation and its
/// pending AI suggestion, it evaluates every gate (activation, license, suggestion presence,
/// confidence, conversation state, schema-leaf validity) and returns a committing or skipping
/// <see cref="AutonomousDispositionDecision"/>. It is <b>pure</b>: it performs no database write,
/// emits no audit event, and triggers no state transition — the close-path consumer (task group 4)
/// acts on the decision (the conditional/CAS commit and audit emission live there, NOT here).
/// </summary>
/// <remarks>
/// <para>
/// The verification described here is the <i>read-side</i> pass; the worker still performs the
/// state-based conditional write at commit time so a concurrent human typify wins the race
/// (ADR-0034 Decision 6). A <see cref="AutonomousDispositionDecision.ShouldCommit"/> result is
/// therefore necessary-but-not-sufficient: it means "all gates pass as observed", not "the write
/// will land".
/// </para>
/// </remarks>
public interface IAutonomousDispositionPolicy
{
    /// <summary>
    /// Evaluates whether the abandoned wrap-up <paramref name="conversation"/> should be stamped
    /// with the pending AI <paramref name="suggestion"/>.
    /// </summary>
    /// <param name="conversation">
    /// The timed-out conversation under consideration. <b>Passed by the caller</b> (the worker
    /// already holds the entity from its candidate query) rather than re-fetched — the policy reads
    /// its <see cref="Conversation.State"/> and <see cref="Conversation.TenantId"/> to gate on
    /// state and to scope the activation/schema lookups.
    /// </param>
    /// <param name="suggestion">
    /// The conversation's latest pending AI suggestion, or <see langword="null"/> if none exists.
    /// <b>Passed by the caller</b> (resolved once via <see cref="IAiSuggestionStore"/>) so the
    /// policy stays a pure decision over already-fetched inputs.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A committing decision (carrying the leaf node id, node path, and confidence to stamp) when
    /// every gate passes, otherwise a skipping decision carrying the first failing gate's
    /// <see cref="AutonomousSkipReason"/>.
    /// </returns>
    Task<AutonomousDispositionDecision> EvaluateAsync(
        Conversation conversation,
        AiSuggestionRecord? suggestion,
        CancellationToken ct);
}
