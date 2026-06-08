using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Stores;

/// <summary>Persistence abstraction for schema-to-scope bindings.</summary>
public interface ISchemaBindingStore
{
    Task<SchemaBinding?> GetByIdAsync(TenantId tenantId, EntityId bindingId, CancellationToken ct);

    Task<IReadOnlyList<SchemaBinding>> ListAsync(TenantId tenantId, CancellationToken ct);

    /// <summary>Lists bindings matching a scope (and optional scope ref) for resolution.</summary>
    Task<IReadOnlyList<SchemaBinding>> ListByScopeAsync(TenantId tenantId, BindingScope scope, string? scopeRef, CancellationToken ct);

    Task SaveAsync(SchemaBinding binding, CancellationToken ct);

    Task DeleteAsync(TenantId tenantId, EntityId bindingId, CancellationToken ct);
}
