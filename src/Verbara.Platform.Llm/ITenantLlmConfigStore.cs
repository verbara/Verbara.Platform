using Verbara.Platform.Core;

namespace Verbara.Platform.Llm;

/// <summary>
/// Persistence abstraction for per-tenant BYO LLM config (P2c.1).
/// Implementations: <c>InMemoryTenantLlmConfigStore</c> (dev/test),
/// <c>PostgresTenantLlmConfigStore</c> (production — API key encrypted at rest).
/// </summary>
public interface ITenantLlmConfigStore
{
    /// <summary>
    /// Returns the tenant's LLM config (decrypted <c>ApiKey</c> materialised by the store),
    /// or <see langword="null"/> if the tenant has no row (a valid, non-error "AI off" state).
    /// </summary>
    Task<TenantLlmConfig?> GetAsync(EntityId tenantId, CancellationToken ct);

    /// <summary>Inserts or replaces the tenant's config (encrypts the key at rest).</summary>
    Task UpsertAsync(TenantLlmConfig config, CancellationToken ct);

    /// <summary>Removes the tenant's config row (returns the tenant to manual/agent-driven mode).</summary>
    Task DeleteAsync(EntityId tenantId, CancellationToken ct);
}
