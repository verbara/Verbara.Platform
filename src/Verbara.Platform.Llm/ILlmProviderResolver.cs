using Verbara.Platform.Core;

namespace Verbara.Platform.Llm;

/// <summary>
/// Resolves a tenant's BYO (bring-your-own) LLM provider at call time (P2c.1, Architecture A).
/// Reads the tenant's <see cref="TenantLlmConfig"/>, selects the concrete <see cref="ILlmProvider"/>
/// for its <see cref="ProviderType"/>, decrypts the key once at build time, and caches the result.
/// <para>
/// <strong>Fail-closed:</strong> a tenant with no config or <c>Enabled == false</c> resolves to
/// <see langword="null"/> — a valid, non-error "AI off" state the caller degrades on (the
/// typification classifier returns an empty suggestion). AI is strictly opt-in.
/// </para>
/// </summary>
public interface ILlmProviderResolver
{
    /// <summary>
    /// Resolves the tenant's configured provider, or <see langword="null"/> when the tenant has no
    /// config or has AI disabled. The returned <see cref="ResolvedLlmProvider"/> also carries the
    /// configured model id (for classification provenance).
    /// </summary>
    Task<ResolvedLlmProvider?> ResolveAsync(EntityId tenantId, CancellationToken ct);

    /// <summary>
    /// Evicts every cached entry for the tenant so the next resolve rebuilds from the store.
    /// Called after the admin API upserts or deletes the tenant's config.
    /// </summary>
    void Invalidate(EntityId tenantId);

    /// <summary>
    /// Builds a concrete <see cref="ILlmProvider"/> from an arbitrary <see cref="TenantLlmConfig"/>
    /// WITHOUT touching the store or the per-tenant cache. The admin "test connection" endpoint uses
    /// this to probe a SAVED or a DRAFT config (a draft key supplied in the request body is used
    /// in-memory only and is never persisted or logged). Shares the exact same provider-construction
    /// switch as <see cref="ResolveAsync"/>, so a probe exercises the same wire path a real classify
    /// call would. The returned provider's decrypted key lives only for the probe's lifetime.
    /// </summary>
    ILlmProvider BuildTransient(TenantLlmConfig config);
}

/// <summary>
/// A resolved per-tenant provider plus the model id its config declared (used for the
/// classification provenance <c>ModelId</c>).
/// </summary>
/// <param name="Provider">The concrete provider built for the tenant's config.</param>
/// <param name="ModelId">The model identifier from the tenant's config (e.g. <c>gpt-4o-mini</c>).</param>
public sealed record ResolvedLlmProvider(ILlmProvider Provider, string ModelId);
