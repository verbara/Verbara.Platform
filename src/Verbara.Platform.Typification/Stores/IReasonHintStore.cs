using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Stores;

/// <summary>Persistence abstraction for routing-context-to-reason hints.</summary>
public interface IReasonHintStore
{
    Task<ReasonHint?> GetByIdAsync(TenantId tenantId, EntityId hintId, CancellationToken ct);

    Task<IReadOnlyList<ReasonHint>> ListAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>Lists hints matching a scope and scope ref for prefill resolution.</summary>
    Task<IReadOnlyList<ReasonHint>> ListByScopeAsync(TenantId tenantId, ReasonHintScope scope, string scopeRef, CancellationToken ct);

    Task SaveAsync(ReasonHint hint, CancellationToken ct);

    Task DeleteAsync(TenantId tenantId, EntityId hintId, CancellationToken ct);
}
