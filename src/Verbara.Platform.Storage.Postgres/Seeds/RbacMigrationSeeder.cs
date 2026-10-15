using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
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

    private static readonly string[] s_clonedTemplates =
        ["agent", "supervisor", "quality_analyst", "manager", "admin", "system_admin", "api"];

    public static Task MigrateExistingUsersAsync(NpgsqlDataSource dataSource, CancellationToken ct = default) =>
        MigrateExistingUsersAsync(dataSource, NullLogger.Instance, ct);

    /// <summary>
    /// Clones the role templates into every tenant that has users and attaches each user to the role
    /// their <c>users.role</c> maps to. Each tenant runs in its own transaction: a tenant that fails is
    /// rolled back whole, logged (<see cref="RbacMigrationLog.TenantFailed"/>) and retried at the next
    /// start, and the loop continues with the next tenant. Once every tenant was tried, a failure is
    /// reported to the caller as one <see cref="InvalidOperationException"/>, so the host still logs the
    /// seed as failed.
    /// </summary>
    public static async Task MigrateExistingUsersAsync(NpgsqlDataSource dataSource, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(logger);

        // 1. Get all distinct tenants from users table. Ordered, so a run visits tenants in a stable order.
        var tenants = await dataSource.QueryListAsync(
            "SELECT DISTINCT tenant_id FROM users ORDER BY tenant_id",
            static _ => { },
            static r => r.GetString("tenant_id"), ct);

        var failed = new List<string>();
        foreach (var tenantId in tenants)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await MigrateTenantAsync(conn, tx, tenantId, logger, ct);
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The transaction was disposed uncommitted: none of this tenant's writes survive, and the
                // next start retries it from where it stood before this run.
                RbacMigrationLog.TenantFailed(logger, tenantId, ex);
                failed.Add(tenantId);
            }
        }

        if (failed.Count > 0)
        {
            var list = string.Join(", ", failed);
            RbacMigrationLog.MigrationIncomplete(logger, failed.Count, tenants.Count, list);
            throw new InvalidOperationException(
                $"RBAC role migration failed for {failed.Count} of {tenants.Count} tenants ({list}); " +
                "they were rolled back and the other tenants were migrated.");
        }
    }

    private static async Task MigrateTenantAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, ILogger logger, CancellationToken ct)
    {
        // 2. Clone all 7 templates as tenant roles (if not already cloned)
        foreach (var templateId in s_clonedTemplates)
        {
            var cloned = await conn.ExecuteAsync(
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
                tx, ct);

            if (cloned == 0 && await RoleBlockingCloneAsync(conn, tx, tenantId, templateId, ct) is { } existingRoleId)
                RbacMigrationLog.CloneSkipped(logger, tenantId, templateId, existingRoleId);

            await conn.ExecuteAsync(
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
                tx, ct);
        }

        // 3. Assign users to roles based on their existing UserRole enum value
        var users = await UsersOfAsync(conn, tx, tenantId, ct);

        foreach (var (userId, roleEnum) in users)
        {
            if (s_roleEnumToTemplate.TryGetValue(roleEnum, out var templateId))
            {
                // RETURNING names the role this run attached (null when the user was settled), which the
                // name-dependent check below needs; the facade's ExecuteAsync returns a count only.
                await using var attach = new NpgsqlCommand(
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
                    // Convergence: a user who already holds a copy of the template — under the template
                    // id, the provisioned role_{template}_{tenant}, the id setup gives it
                    // (admin-{tenant}), or the template's name; the shapes DefaultTenantRole recognises —
                    // is settled. Without this, a run after the tenant creates the exact template id, or
                    // renames the equivalent role, resolves through the other branch and adds a second
                    // row that ON CONFLICT cannot catch (the role_id differs).
                    // A grant the migration or user creation made is also settled when its role was cloned
                    // from one of the templates that come with the user's role (source_template_id), whatever
                    // the role is called now: a rename keeps the source. An administrator's grant on such a
                    // role is that administrator's custom role and settles nothing through its source.
                    "  AND NOT EXISTS (" +
                    "    SELECT 1 FROM user_roles held " +
                    "    JOIN tenant_roles htr ON htr.tenant_id = held.tenant_id AND htr.role_id = held.role_id " +
                    "    WHERE held.tenant_id = @TenantId AND held.user_id = @UserId " +
                    "      AND (htr.role_id = @RoleId " +
                    "           OR htr.role_id = 'role_' || @RoleId || '_' || @TenantId " +
                    "           OR (@RoleId = 'admin' AND htr.role_id = 'admin-' || @TenantId) " +
                    "           OR lower(htr.name) = lower((SELECT name FROM role_templates WHERE template_id = @RoleId)) " +
                    "           OR (held.assigned_by IN (@Migration, @DefaultRole) AND htr.source_template_id = ANY(@Templates)))) " +
                    // Exact id wins over the name match when both are present.
                    "ORDER BY (tr.role_id = @RoleId) DESC " +
                    "LIMIT 1 " +
                    "ON CONFLICT (tenant_id, user_id, role_id) DO NOTHING " +
                    "RETURNING role_id",
                    conn, tx);
                attach.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
                attach.Parameters.Add(new NpgsqlParameter("UserId", userId));
                attach.Parameters.Add(new NpgsqlParameter("RoleId", templateId));
                attach.Parameters.Add(new NpgsqlParameter("Migration", MigrationAssigner));
                attach.Parameters.Add(new NpgsqlParameter("DefaultRole", DefaultTenantRole.AssignedBy));
                attach.Parameters.Add(new NpgsqlParameter("Templates", NpgsqlDbType.Array | NpgsqlDbType.Text)
                {
                    Value = DefaultTenantRole.TemplatesGrantedWith((UserRole)roleEnum).ToArray(),
                });

                if (await attach.ExecuteScalarAsync(ct) is string attachedRoleId
                    && await NameDependentGrantsAsync(conn, tx, tenantId, userId, attachedRoleId, ct) is { } heldRoleIds)
                    RbacMigrationLog.NameDependentAttach(logger, tenantId, userId, attachedRoleId, heldRoleIds);
            }
        }

        // 4. Take back the grants step 3 or user creation made for a role the user no longer has.
        await conn.ExecuteAsync(
            s_removeGrantsOfFormerRolesSql,
            p =>
            {
                p.Add(new NpgsqlParameter("TenantId", tenantId));
                p.Add(new NpgsqlParameter("Migration", MigrationAssigner));
                p.Add(new NpgsqlParameter("DefaultRole", DefaultTenantRole.AssignedBy));
            },
            tx, ct);
    }

    // The facade has no connection-scoped query, and these reads must run inside the tenant's
    // transaction, so they use a raw (AOT-safe) NpgsqlCommand — the shape
    // RoleTemplateSeeder.ReseedExistingTenantsAsync uses.
    private static async Task<List<(string UserId, int Role)>> UsersOfAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT user_id, role FROM users WHERE tenant_id = @TenantId", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        var users = new List<(string UserId, int Role)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            users.Add((reader.GetString("user_id"), reader.GetInt32("role")));
        return users;
    }

    // The role that made the clone of templateId a no-op: one named like the template under another id,
    // while no role carries the template id. Null when the clone already exists (a normal re-run) or the
    // template is unknown — neither is a skip worth recording.
    private static async Task<string?> RoleBlockingCloneAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, string templateId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT tr.role_id FROM tenant_roles tr " +
            "JOIN role_templates rt ON rt.template_id = @TemplateId " +
            "WHERE tr.tenant_id = @TenantId AND tr.role_id <> @TemplateId AND lower(tr.name) = lower(rt.name) " +
            "  AND NOT EXISTS (SELECT 1 FROM tenant_roles own WHERE own.tenant_id = @TenantId AND own.role_id = @TemplateId) " +
            "LIMIT 1",
            conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TemplateId", templateId));
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // The grants this user holds that the migration or user creation made on a role other than the one
    // just attached, which no id shape, name or source template ties to any UserRole any more
    // (default_roles): a grant only its role's former name tied to one. Every grant step 3 and user
    // creation make is on a role resolved by the template's id or name, so a grant default_roles does not
    // contain lost its tie through a rename (of a role with no source template, or one cloned from a
    // template no UserRole comes with). Null when there is none.
    private static async Task<string?> NameDependentGrantsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string tenantId, string userId, string attachedRoleId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "WITH " + s_defaultRolesCte + " " +
            "SELECT string_agg(held.role_id, ', ' ORDER BY held.role_id) " +
            "FROM user_roles held " +
            "WHERE held.tenant_id = @TenantId AND held.user_id = @UserId AND held.role_id <> @AttachedRoleId " +
            "  AND held.assigned_by IN (@Migration, @DefaultRole) " +
            "  AND NOT EXISTS (SELECT 1 FROM default_roles d WHERE d.role_id = held.role_id)",
            conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("UserId", userId));
        cmd.Parameters.Add(new NpgsqlParameter("AttachedRoleId", attachedRoleId));
        cmd.Parameters.Add(new NpgsqlParameter("Migration", MigrationAssigner));
        cmd.Parameters.Add(new NpgsqlParameter("DefaultRole", DefaultTenantRole.AssignedBy));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private const string MigrationAssigner = DefaultTenantRole.MigrationAssignedBy;

    // default_roles: for each UserRole, the tenant roles that come with it — the shapes
    // DefaultTenantRole.RolesGrantedWith recognises for each of its templates (the template id,
    // role_{template}_{tenant}, the id setup gives it, the template's name) and every role cloned from one
    // of those templates (source_template_id, which a rename keeps). Its readers only ever apply it to
    // grants the migration or user creation made: the source alone does not make an administrator's
    // custom role a default. role_templates_of is generated from DefaultTenantRole (TemplatesGrantedWith
    // and SetupRoleIdPrefixFor over every UserRole), so the C# and SQL sides share one list.
    private static readonly string s_defaultRolesCte =
        "role_templates_of (role, template_id, setup_prefix) AS (VALUES " + RoleTemplatesOfRows() + "), " +
        "default_roles AS (" +
        "  SELECT m.role, tr.role_id " +
        "  FROM role_templates_of m " +
        "  LEFT JOIN role_templates rt ON rt.template_id = m.template_id " +
        "  JOIN tenant_roles tr ON tr.tenant_id = @TenantId " +
        "   AND (tr.role_id = m.template_id " +
        "        OR tr.role_id = 'role_' || m.template_id || '_' || @TenantId " +
        "        OR tr.role_id = m.setup_prefix || @TenantId " +
        "        OR lower(tr.name) = lower(rt.name) " +
        "        OR tr.source_template_id = m.template_id))";

    // One VALUES row per (UserRole, template) pair: (0, 'agent', NULL), (2, 'admin', 'admin-'), ...
    // The values are compile-time literals of DefaultTenantRole, never input; a quote in one would be a
    // programming error, so it fails the type's initialisation rather than reach the SQL.
    private static string RoleTemplatesOfRows()
    {
        var rows = new List<string>();
        foreach (var role in Enum.GetValues<UserRole>())
        {
            foreach (var templateId in DefaultTenantRole.TemplatesGrantedWith(role))
            {
                var prefix = DefaultTenantRole.SetupRoleIdPrefixFor(templateId);
                rows.Add($"({(int)role}, {SqlLiteral(templateId)}, {(prefix is null ? "NULL::text" : SqlLiteral(prefix))})");
            }
        }

        return string.Join(", ", rows);
    }

    private static string SqlLiteral(string value) =>
        value.Contains('\'', StringComparison.Ordinal)
            ? throw new InvalidOperationException($"Role template literal '{value}' must not contain a quote.")
            : $"'{value}'";

    // A grant this migration (assigned_by 'migration') or a user-creation path ('default-role') made
    // follows from users.role, so once users.role changes it is stale. A role change used to write
    // users.role only, and step 3 then added the new role's grant beside the old one, at every start.
    // Removed: such a grant on a default role of another UserRole (default_roles), unless that role is
    // also a default role of the user's own. Kept: grants anyone else made — an administrator's (its id),
    // and setup's (no assigner) — which say nothing about following users.role, and roles no UserRole
    // claims. Step 3 settles only on a grant this keeps, so a demotion converges in one run. Idempotent.
    private static readonly string s_removeGrantsOfFormerRolesSql =
        "WITH " + s_defaultRolesCte + " " +
        "DELETE FROM user_roles ur " +
        "USING users u " +
        "WHERE ur.tenant_id = @TenantId " +
        "  AND u.tenant_id = ur.tenant_id AND u.user_id = ur.user_id " +
        "  AND u.role IN (SELECT role FROM role_templates_of) " +
        "  AND ur.assigned_by IN (@Migration, @DefaultRole) " +
        "  AND EXISTS (SELECT 1 FROM default_roles d WHERE d.role_id = ur.role_id AND d.role <> u.role) " +
        "  AND NOT EXISTS (SELECT 1 FROM default_roles d WHERE d.role_id = ur.role_id AND d.role = u.role)";
}
