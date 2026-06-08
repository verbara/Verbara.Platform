using Verbara.Platform.Core;

namespace Verbara.Platform.Typification.Stores;

/// <summary>Persistence abstraction for versioned typification schemas.</summary>
public interface ITypificationSchemaStore
{
    /// <summary>
    /// Gets a schema by id; when <paramref name="version"/> is null, returns the
    /// latest version (published or not).
    /// </summary>
    Task<TypificationSchema?> GetByIdAsync(TenantId tenantId, EntityId schemaId, int? version, CancellationToken ct);

    /// <summary>Gets the latest published version of a schema, if any.</summary>
    Task<TypificationSchema?> GetLatestPublishedAsync(TenantId tenantId, EntityId schemaId, CancellationToken ct);

    Task<IReadOnlyList<TypificationSchema>> ListAsync(TenantId tenantId, CancellationToken ct);

    Task SaveAsync(TypificationSchema schema, CancellationToken ct);

    Task DeleteAsync(TenantId tenantId, EntityId schemaId, CancellationToken ct);
}
