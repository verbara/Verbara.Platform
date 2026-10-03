using Npgsql;
using Verbara.Platform.Identity;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Seeds;

internal static class RbacMigrationSeeder
{
    private static readonly Dictionary<int, string> s_roleEnumToTemplate = new()
    {
        [0] = "agent",       // UserRole.Agent
        [1] = "supervisor",  // UserRole.Supervisor
        [2] = "admin",       // UserRole.Admin
        [3] = "api",         // UserRole.Api
    };

    public static async Task MigrateExistingUsersAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        // 1. Get all distinct tenants from users table
        var tenants = await dataSource.QueryListAsync(
            "SELECT DISTINCT tenant_id FROM users",
            static _ => { },
            static r => r.GetString("tenant_id"), ct);

        foreach (var tenantId in tenants)
        {
            // 2. Clone all 7 templates as tenant roles (if not already cloned)
            foreach (var templateId in new[]
                { "agent", "supervisor", "quality_analyst", "manager", "admin", "system_admin", "api" })
            {
                await dataSource.ExecuteAsync(
                    "INSERT INTO tenant_roles (role_id, tenant_id, name, description, source_template_id, is_default, created_at) " +
                    "SELECT @TemplateId, @TenantId, rt.name, rt.description, @TemplateId, true, now() " +
                    "FROM role_templates rt WHERE rt.template_id = @TemplateId " +
                    // Untargeted DO NOTHING on purpose: tenant_roles carries a SECOND unique
                    // index, idx_tenant_roles_name on (tenant_id, lower(name)). A tenant
                    // provisioned outside this seeder can already hold an equivalent role under a
                    // different id (the `demo` tenant has `admin-demo` named "Admin"), so a clone
                    // of template `admin` misses the (tenant_id, role_id) conflict target and
                    // raises 23505 instead — which aborted the whole migration loop mid-tenant.
                    "ON CONFLICT DO NOTHING",
                    p =>
                    {
                        p.Add(new NpgsqlParameter("TemplateId", templateId));
                        p.Add(new NpgsqlParameter("TenantId", tenantId));
                    },
                    ct);

                await dataSource.ExecuteAsync(
                    "INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id) " +
                    "SELECT @TenantId, @TemplateId, rtp.permission_id " +
                    "FROM role_template_permissions rtp WHERE rtp.template_id = @TemplateId " +
                    // The clone above is skipped when the tenant already owns that role under a
                    // different id, so the target row may not exist. Without this guard the grant
                    // insert would violate tenant_role_permissions_tenant_id_role_id_fkey (23503).
                    "AND EXISTS (SELECT 1 FROM tenant_roles tr " +
                    "            WHERE tr.tenant_id = @TenantId AND tr.role_id = @TemplateId) " +
                    "ON CONFLICT (tenant_id, role_id, permission_id) DO NOTHING",
                    p =>
                    {
                        p.Add(new NpgsqlParameter("TenantId", tenantId));
                        p.Add(new NpgsqlParameter("TemplateId", templateId));
                    },
                    ct);
            }

            // 3. Assign users to roles based on their existing UserRole enum value
            var users = await dataSource.QueryListAsync(
                "SELECT user_id, role FROM users WHERE tenant_id = @TenantId",
                p => p.Add(new NpgsqlParameter("TenantId", tenantId)),
                static r => (user_id: r.GetString("user_id"), role: r.GetInt32("role")), ct);

            foreach (var (userId, roleEnum) in users)
            {
                if (s_roleEnumToTemplate.TryGetValue(roleEnum, out var templateId))
                {
                    await dataSource.ExecuteAsync(
                        "INSERT INTO user_roles (tenant_id, user_id, role_id, assigned_at, assigned_by) " +
                        // Resolve the role rather than assume the clone landed under @RoleId. When
                        // the tenant already owned an equivalent role the clone above was skipped,
                        // and assigning @RoleId blind would violate user_roles_tenant_id_role_id_fkey
                        // (23503). Falling back to the same-named role is what the migration means:
                        // a legacy users.role of Admin should attach to whatever this tenant calls
                        // "Admin" (`admin-demo` on the demo tenant). Skipping instead would leave a
                        // user who reads as Admin holding zero permissions — the silent
                        // RoleDefaultPermissions fallback ADR-0037 exists to eliminate.
                        // Selecting from tenant_roles also subsumes the EXISTS guard: no candidate
                        // row means no insert.
                        "SELECT @TenantId, @UserId, tr.role_id, now(), @Migration " +
                        "FROM tenant_roles tr " +
                        "WHERE tr.tenant_id = @TenantId " +
                        "  AND (tr.role_id = @RoleId OR lower(tr.name) = lower(" +
                        "        (SELECT name FROM role_templates WHERE template_id = @RoleId))) " +
                        // Exact id wins over the name match when both are present.
                        "ORDER BY (tr.role_id = @RoleId) DESC " +
                        "LIMIT 1 " +
                        "ON CONFLICT (tenant_id, user_id, role_id) DO NOTHING",
                        p =>
                        {
                            p.Add(new NpgsqlParameter("TenantId", tenantId));
                            p.Add(new NpgsqlParameter("UserId", userId));
                            p.Add(new NpgsqlParameter("RoleId", templateId));
                            p.Add(new NpgsqlParameter("Migration", MigrationAssigner));
                        },
                        ct);
                }
            }

            // 4. Take back the grants step 3 or user creation made for a role the user no longer has.
            await dataSource.ExecuteAsync(
                RemoveGrantsOfFormerRolesSql,
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId));
                    p.Add(new NpgsqlParameter("Migration", MigrationAssigner));
                    p.Add(new NpgsqlParameter("DefaultRole", DefaultTenantRole.AssignedBy));
                },
                ct);
        }
    }

    private const string MigrationAssigner = "migration";

    // A grant this migration (assigned_by 'migration') or a user-creation path ('default-role') made
    // follows from users.role, so once users.role changes it is stale. A role change used to write
    // users.role only, and step 3 then added the new role's grant beside the old one, at every start.
    // Removed: such a grant on the role step 3 resolves for another UserRole (the template's id, or the
    // role named like the template), unless that role is also the one it resolves for the user's own.
    // Kept: grants anyone else made — an administrator's (its id), and setup's (no assigner) — which
    // say nothing about following users.role, and roles no UserRole resolves to. Idempotent.
    private const string RemoveGrantsOfFormerRolesSql =
        "WITH role_templates_of (role, template_id) AS (" +
        "  VALUES (0, 'agent'), (1, 'supervisor'), (2, 'admin'), (3, 'api')), " +
        "default_roles AS (" +
        "  SELECT m.role, tr.role_id " +
        "  FROM role_templates_of m " +
        "  JOIN role_templates rt ON rt.template_id = m.template_id " +
        "  JOIN tenant_roles tr ON tr.tenant_id = @TenantId " +
        "   AND (tr.role_id = m.template_id OR lower(tr.name) = lower(rt.name))) " +
        "DELETE FROM user_roles ur " +
        "USING users u " +
        "WHERE ur.tenant_id = @TenantId " +
        "  AND u.tenant_id = ur.tenant_id AND u.user_id = ur.user_id " +
        "  AND u.role IN (0, 1, 2, 3) " +
        "  AND ur.assigned_by IN (@Migration, @DefaultRole) " +
        "  AND EXISTS (SELECT 1 FROM default_roles d WHERE d.role_id = ur.role_id AND d.role <> u.role) " +
        "  AND NOT EXISTS (SELECT 1 FROM default_roles d WHERE d.role_id = ur.role_id AND d.role = u.role)";
}
