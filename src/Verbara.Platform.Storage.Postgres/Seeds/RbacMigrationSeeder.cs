using Npgsql;
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
                    "ON CONFLICT (tenant_id, role_id) DO NOTHING",
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
                        "VALUES (@TenantId, @UserId, @RoleId, now(), 'migration') " +
                        "ON CONFLICT (tenant_id, user_id, role_id) DO NOTHING",
                        p =>
                        {
                            p.Add(new NpgsqlParameter("TenantId", tenantId));
                            p.Add(new NpgsqlParameter("UserId", userId));
                            p.Add(new NpgsqlParameter("RoleId", templateId));
                        },
                        ct);
                }
            }
        }
    }
}
