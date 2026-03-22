namespace Asterisk.Platform.KnowledgeBase;

/// <summary>
/// Converts text into a dense embedding vector for use in semantic / RAG search pipelines.
/// Consumers provide their own implementation (OpenAI, local model, etc.).
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Returns an embedding vector for the supplied <paramref name="text"/>.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}
