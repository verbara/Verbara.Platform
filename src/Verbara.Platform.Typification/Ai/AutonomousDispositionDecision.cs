using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// The outcome of <see cref="IAutonomousDispositionPolicy.EvaluateAsync"/>: a discriminated result
/// that either <b>commits</b> (all gates passed — carries the chosen leaf node id, node path, and
/// confidence to stamp) or <b>skips</b> (carries the first failing gate's <see cref="AutonomousSkipReason"/>).
/// The decision is <b>pure</b>: producing it performs no write, no audit emission, and no state
/// transition — the close-path consumer (task group 4) acts on it.
/// </summary>
/// <remarks>
/// Construct via the <see cref="Commit"/> / <see cref="Skip"/> factories rather than the primary
/// constructor so the two arms stay internally consistent (a committing decision always has a leaf
/// and a null reason; a skipping decision always has a reason and a null leaf).
/// </remarks>
public sealed record AutonomousDispositionDecision
{
    private AutonomousDispositionDecision(
        bool shouldCommit,
        EntityId? leafNodeId,
        IReadOnlyList<string>? nodePath,
        double confidence,
        AutonomousSkipReason? reason)
    {
        ShouldCommit = shouldCommit;
        LeafNodeId = leafNodeId;
        NodePath = nodePath;
        Confidence = confidence;
        Reason = reason;
    }

    /// <summary><see langword="true"/> when every gate passed and a disposition should be stamped.</summary>
    public bool ShouldCommit { get; }

    /// <summary>
    /// The leaf node to stamp when <see cref="ShouldCommit"/> is <see langword="true"/>;
    /// <see langword="null"/> when skipping.
    /// </summary>
    public EntityId? LeafNodeId { get; }

    /// <summary>
    /// The full root→leaf node path to stamp when <see cref="ShouldCommit"/> is
    /// <see langword="true"/>; <see langword="null"/> when skipping.
    /// </summary>
    public IReadOnlyList<string>? NodePath { get; }

    /// <summary>
    /// The suggestion confidence to record when <see cref="ShouldCommit"/> is <see langword="true"/>;
    /// <c>0</c> when skipping.
    /// </summary>
    public double Confidence { get; }

    /// <summary>
    /// The first failing gate's reason when <see cref="ShouldCommit"/> is <see langword="false"/>;
    /// <see langword="null"/> when committing.
    /// </summary>
    public AutonomousSkipReason? Reason { get; }

    /// <summary>Creates a committing decision carrying the leaf to stamp.</summary>
    public static AutonomousDispositionDecision Commit(
        EntityId leafNodeId,
        IReadOnlyList<string> nodePath,
        double confidence) =>
        new(shouldCommit: true, leafNodeId, nodePath, confidence, reason: null);

    /// <summary>Creates a skipping decision carrying the first failing gate's reason.</summary>
    public static AutonomousDispositionDecision Skip(AutonomousSkipReason reason) =>
        new(shouldCommit: false, leafNodeId: null, nodePath: null, confidence: 0, reason);
}
