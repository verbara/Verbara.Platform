namespace Verbara.Platform.Storage.Postgres.Seeds;

/// <summary>
/// Read-only projection of the permission ids catalogued by the seeder — i.e. the
/// <c>permission_id</c> values written to the <c>permissions</c> table.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0037 — <c>role_template_permissions</c> and <c>tenant_role_permissions</c> both carry a
/// foreign key onto <c>permissions</c>. A permission id that is granted by a role template, or
/// named by an authorization gate, but is <em>not</em> in this set can never be satisfied by any
/// principal, and on the grant path it aborts the whole un-transacted seeding loop with a
/// <c>23503</c> foreign-key violation. Catalog membership is therefore an invariant enforced by
/// tests rather than by review.
/// </para>
/// <para>
/// <c>PermissionSeeder</c> itself is <c>internal</c>, and <c>Verbara.Platform.Api.Tests</c> only
/// references <c>Verbara.Platform.Api</c>, so this public accessor is the seam both test projects
/// use to assert membership without duplicating the list. The set is built once into a static
/// field, uses ordinal comparison, and involves no reflection (Native AOT safe).
/// </para>
/// </remarks>
public static class PermissionCatalog
{
    private static readonly HashSet<string> CatalogIds = BuildIds();

    /// <summary>Every permission id the seeder writes to the <c>permissions</c> table.</summary>
    public static IReadOnlySet<string> Ids => CatalogIds;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="permissionId"/> is catalogued and can
    /// therefore be granted to a role or named by an authorization gate.
    /// </summary>
    /// <param name="permissionId">The <c>domain:resource:action</c> id to test.</param>
    public static bool Contains(string permissionId) => CatalogIds.Contains(permissionId);

    private static HashSet<string> BuildIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in PermissionSeeder.GetPermissions())
            ids.Add(permission.PermissionId);
        return ids;
    }
}
