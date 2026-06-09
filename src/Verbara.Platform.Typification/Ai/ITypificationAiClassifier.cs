using Verbara.Platform.Conversations;
using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Ai;

/// <summary>
/// Direct LLM classifier (P2a): maps a conversation transcript to a typification leaf
/// node path + captured field values + confidence + sentiment.
/// <para>
/// Implementations MUST be best-effort and degrade gracefully — any failure (no
/// transcript text, malformed model output, an unknown/non-leaf code, a leaf outside the
/// bound sub-tree, or a transport/timeout exception) yields <see langword="null"/> rather
/// than throwing, so callers can fall back to manual disposition.
/// </para>
/// </summary>
public interface ITypificationAiClassifier
{
    /// <summary>
    /// Classifies the supplied transcript against <paramref name="schema"/>.
    /// </summary>
    /// <param name="schema">The published taxonomy to classify against.</param>
    /// <param name="subtreeRoot">
    /// When non-null, restrict the candidate leaves to those under this node and require
    /// the resolved path to pass through it; a leaf outside this branch yields
    /// <see langword="null"/>.
    /// </param>
    /// <param name="conversation">The conversation being typified (provides the channel).</param>
    /// <param name="transcript">The ordered messages forming the transcript text.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A validated <see cref="AiClassification"/>, or <see langword="null"/>.</returns>
    Task<AiClassification?> ClassifyAsync(
        TypificationSchema schema,
        EntityId? subtreeRoot,
        Conversation conversation,
        IReadOnlyList<Message> transcript,
        CancellationToken ct);
}
