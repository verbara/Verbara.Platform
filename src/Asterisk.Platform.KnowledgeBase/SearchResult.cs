namespace Asterisk.Platform.KnowledgeBase;

/// <summary>
/// An article returned by a knowledge-base search together with its relevance score.
/// </summary>
/// <param name="Article">The matched article.</param>
/// <param name="Score">Normalised relevance score in the range [0, 1].</param>
public sealed record SearchResult(Article Article, double Score);
