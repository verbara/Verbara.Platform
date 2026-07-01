using Npgsql;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Seeds;

/// <summary>
/// Seeds the eleven canonical RBAC role templates and their permissions, plus the
/// per-tenant <c>platform_admin</c> permission re-seed used by the
/// <c>RbacReseed</c> CLI tool to migrate existing tenants when
/// <see cref="AllPermissions"/> grows.
/// </summary>
public static class RoleTemplateSeeder
{
    /// <summary>Result of <see cref="ReseedExistingTenantsAsync"/> for a single tenant.</summary>
    /// <param name="PermissionsAdded">Number of <c>tenant_role_permissions</c> rows inserted.</param>
    /// <param name="PermissionsRemoved">Number of <c>tenant_role_permissions</c> rows deleted.</param>
    public sealed record ReseedResult(int PermissionsAdded, int PermissionsRemoved);

    /// <summary>Role id used inside <c>tenant_roles</c> for the platform-admin role.</summary>
    /// <remarks>
    /// Matches the <c>template_id</c> seeded by <see cref="GetTemplates"/> and the
    /// <c>role_id</c> cloned per-tenant by <see cref="RbacMigrationSeeder.MigrateExistingUsersAsync"/>.
    /// </remarks>
    public const string PlatformAdminRoleId = "platform_admin";

    public static async Task SeedAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        const string templateSql =
            "INSERT INTO role_templates (template_id, name, description, is_system, created_at) " +
            "VALUES (@TemplateId, @Name, @Description, true, now()) " +
            "ON CONFLICT (template_id) DO NOTHING";

        const string permSql =
            "INSERT INTO role_template_permissions (template_id, permission_id) " +
            "VALUES (@TemplateId, @PermissionId) " +
            "ON CONFLICT (template_id, permission_id) DO NOTHING";

        // No transaction in the original — each INSERT auto-commits. We reproduce
        // the per-row loop with the facade's connection-less ExecuteAsync.
        foreach (var (template, permissions) in GetTemplates())
        {
            await dataSource.ExecuteAsync(
                templateSql,
                p =>
                {
                    p.Add(new NpgsqlParameter("TemplateId", template.TemplateId));
                    p.Add(new NpgsqlParameter("Name", template.Name));
                    p.Add(new NpgsqlParameter("Description", template.Description));
                },
                ct);
            foreach (var permissionId in permissions)
            {
                await dataSource.ExecuteAsync(
                    permSql,
                    p =>
                    {
                        p.Add(new NpgsqlParameter("TemplateId", template.TemplateId));
                        p.Add(new NpgsqlParameter("PermissionId", permissionId));
                    },
                    ct);
            }
        }
    }

    /// <summary>
    /// Re-aligns the per-tenant <c>platform_admin</c> role's
    /// <c>tenant_role_permissions</c> rows with the current canonical
    /// <see cref="AllPermissions"/> list. For each provided tenant id this method
    /// removes rows whose <c>permission_id</c> is no longer in the canonical list
    /// and inserts rows for any canonical permission that is currently missing.
    /// Tenants without a <c>platform_admin</c> role are skipped (returned in the
    /// dictionary with a zero/zero result for visibility).
    /// </summary>
    /// <remarks>
    /// Used by the <c>tools/RbacReseed</c> CLI to migrate existing tenants when
    /// the canonical permission catalog expands (e.g. R5.2 P0.9 added 7 new keys
    /// that fresh tenants pick up via <see cref="SeedAsync"/> + the migration
    /// seeder, but existing tenants do not). The method is idempotent: a second
    /// invocation against an already-aligned tenant returns
    /// <c>PermissionsAdded == 0</c> and <c>PermissionsRemoved == 0</c>.
    /// Each per-tenant DELETE+INSERT pair runs inside its own transaction so a
    /// failure on one tenant does not corrupt the others already processed.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, ReseedResult>> ReseedExistingTenantsAsync(
        IEnumerable<string> tenantIds,
        NpgsqlConnection conn,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        ArgumentNullException.ThrowIfNull(conn);

        var canonical = AllPermissions();
        var report = new Dictionary<string, ReseedResult>(StringComparer.Ordinal);

        foreach (var tenantId in tenantIds)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
                continue;

            ct.ThrowIfCancellationRequested();

            await using var tx = await conn.BeginTransactionAsync(ct);

            // Confirm the platform_admin role exists for this tenant. If the
            // tenant has not yet been touched by RbacMigrationSeeder we skip
            // (operators are expected to run the orchestrator first).
            const string roleExistsSql =
                "SELECT 1 FROM tenant_roles " +
                "WHERE tenant_id = @TenantId AND role_id = @RoleId LIMIT 1";

            // The facade has no connection-scoped scalar query; this scalar runs
            // inside the per-tenant transaction, so we use a raw (AOT-safe)
            // NpgsqlCommand directly.
            int? exists;
            await using (var existsCmd = new NpgsqlCommand(roleExistsSql, conn, tx))
            {
                existsCmd.Parameters.Add(new NpgsqlParameter("TenantId", tenantId));
                existsCmd.Parameters.Add(new NpgsqlParameter("RoleId", PlatformAdminRoleId));
                var scalar = await existsCmd.ExecuteScalarAsync(ct);
                exists = scalar is null or DBNull ? null : (int)scalar;
            }

            if (exists is null)
            {
                await tx.CommitAsync(ct);
                report[tenantId] = new ReseedResult(0, 0);
                continue;
            }

            const string deleteSql =
                "DELETE FROM tenant_role_permissions " +
                "WHERE tenant_id = @TenantId " +
                "  AND role_id = @RoleId " +
                "  AND permission_id <> ALL(@Canonical)";

            var removed = await conn.ExecuteAsync(
                deleteSql,
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId));
                    p.Add(new NpgsqlParameter("RoleId", PlatformAdminRoleId));
                    p.Add(new NpgsqlParameter("Canonical", canonical));
                },
                tx, ct);

            const string insertSql =
                "INSERT INTO tenant_role_permissions (tenant_id, role_id, permission_id) " +
                "SELECT @TenantId, @RoleId, p " +
                "FROM unnest(@Canonical::text[]) AS p " +
                "ON CONFLICT (tenant_id, role_id, permission_id) DO NOTHING";

            var added = await conn.ExecuteAsync(
                insertSql,
                p =>
                {
                    p.Add(new NpgsqlParameter("TenantId", tenantId));
                    p.Add(new NpgsqlParameter("RoleId", PlatformAdminRoleId));
                    p.Add(new NpgsqlParameter("Canonical", canonical));
                },
                tx, ct);

            await tx.CommitAsync(ct);
            report[tenantId] = new ReseedResult(added, removed);
        }

        return report;
    }

    /// <summary>
    /// Returns the canonical permission catalog used by the
    /// <c>platform_admin</c> role template. Exposed publicly so the
    /// <c>RbacReseed</c> CLI tool can also list / verify the catalog.
    /// </summary>
    public static IReadOnlyList<string> GetCanonicalPermissions() => AllPermissions();

    /// <summary>
    /// Returns the permission ids granted by the canonical role template with the
    /// given <paramref name="templateId"/> (e.g. <c>"admin"</c>,
    /// <c>"platform_admin"</c>), or an empty list if no such template exists.
    /// Exposed for verification (tests / tooling) of template→permission grants
    /// without provisioning Postgres.
    /// </summary>
    public static IReadOnlyList<string> GetTemplatePermissions(string templateId)
    {
        ArgumentNullException.ThrowIfNull(templateId);
        foreach (var (template, permissions) in GetTemplates())
        {
            if (string.Equals(template.TemplateId, templateId, StringComparison.Ordinal))
                return permissions;
        }

        return [];
    }

    private static IEnumerable<(TemplateRow Template, string[] Permissions)> GetTemplates()
    {
        // ── Agent ──
        yield return (
            new TemplateRow("agent", "Agent", "Frontline agent handling conversations"),
            [
                "contacts:conversation:handle",
                "contacts:conversation:transfer",
                "contacts:contact:view",
                "reporting:realtime:view",
            ]);

        // ── Supervisor ──
        yield return (
            new TemplateRow("supervisor", "Supervisor", "Team supervisor with monitoring and reporting access"),
            [
                // Agent permissions
                "contacts:conversation:handle",
                "contacts:conversation:transfer",
                "contacts:contact:view",
                "contacts:contact:edit",
                // Supervisor-specific
                "contacts:conversation:monitor",
                "contacts:conversation:barge",
                "contacts:conversation:whisper",
                "queues:queue:view",
                "queues:queue:edit",
                "queues:member:assign",
                "queues:member:view",
                "queues:member:pause",
                "users:user:view",
                "reporting:realtime:view",
                "reporting:historical:view",
                "quality:evaluation:view",
                "quality:evaluation:score",
                "recording:recording:play",
                "analytics:cdr:view",
                "analytics:interval:view",
                "agentassist:session:view",
                // ADR-0034 — a supervisor may correct an autonomously stamped disposition.
                "typification:correct-autonomous",
            ]);

        // ── Quality Analyst ──
        yield return (
            new TemplateRow("quality_analyst", "Quality Analyst", "QA specialist focused on evaluation and compliance"),
            [
                "quality:evaluation:view",
                "quality:evaluation:score",
                "quality:scorecard:manage",
                "recording:recording:play",
                "recording:recording:export",
                "analytics:cdr:view",
                "callanalytics:analysis:view",
                "reporting:historical:view",
                "reporting:historical:export",
            ]);

        // ── Manager ──
        yield return (
            new TemplateRow("manager", "Manager", "Operations manager with campaign and reporting access"),
            [
                // Supervisor permissions
                "contacts:conversation:handle",
                "contacts:conversation:transfer",
                "contacts:contact:view",
                "contacts:contact:edit",
                "contacts:conversation:monitor",
                "contacts:conversation:barge",
                "contacts:conversation:whisper",
                "queues:queue:view",
                "queues:queue:edit",
                "queues:member:assign",
                "queues:member:view",
                "queues:member:pause",
                "queues:member:delete",
                "users:user:view",
                "users:user:edit",
                "reporting:realtime:view",
                "reporting:historical:view",
                "reporting:historical:export",
                "quality:evaluation:view",
                "quality:evaluation:score",
                "recording:recording:play",
                "analytics:cdr:view",
                "analytics:interval:view",
                "agentassist:session:view",
                // Manager-specific
                "campaigns:campaign:view",
                "campaigns:campaign:edit",
                "campaigns:campaign:execute",
                "routing:skill:view",
                "analytics:cdr:export",
                "callanalytics:analysis:view",
                "typification:ai:configure",
            ]);

        // ── Admin ──
        yield return (
            new TemplateRow("admin", "Admin", "Full administrative access except cluster and auth configuration"),
            AllPermissionsExcept([
                "system:cluster:manage",
                "system:auth:configure",
            ]));

        // ── System Admin ──
        yield return (
            new TemplateRow("system_admin", "System Admin", "Full system access including cluster and auth configuration"),
            AllPermissionsExcept([
                "platform:tenant:create", "platform:tenant:manage",
                "platform:tenant:suspend", "platform:tenant:delete",
                "platform:tenant:impersonate", "platform:server:manage",
                "platform:license:manage", "platform:cluster:manage",
                "features:agent-assist:manage",
            ]));

        // ── Api ──
        yield return (
            new TemplateRow("api", "API", "Machine-to-machine integration with reporting and contact access"),
            [
                "reporting:realtime:view",
                "reporting:historical:view",
                "analytics:cdr:view",
                "analytics:cdr:export",
                "contacts:contact:view",
            ]);

        // ── Platform Admin ──
        // c1 (credit-ledger-topups): platform_admin additionally gets billing:credits:grant
        // (operator top-up minting). It is deliberately NOT in AllPermissions() so the admin /
        // system_admin templates (AllPermissionsExcept) do not inherit the cross-tenant grant.
        yield return (
            new TemplateRow("platform_admin", "Platform Admin", "Full platform administration including cross-tenant operations"),
            [.. AllPermissions(), "billing:credits:grant"]);

        // ── Partner Admin ──
        yield return (
            new TemplateRow("partner_admin", "Partner Admin", "Full partner portal access for managing child customers and billing"),
            [
                "partner:customer:view", "partner:customer:create",
                "partner:customer:manage", "partner:customer:delete",
                "partner:billing:view", "partner:billing:manage",
                "partner:settings:view", "partner:settings:manage",
            ]);

        // ── Partner Billing ──
        yield return (
            new TemplateRow("partner_billing", "Partner Billing", "Partner billing and revenue access without customer management"),
            [
                "partner:customer:view",
                "partner:billing:view", "partner:billing:manage",
            ]);

        // ── Partner Viewer ──
        yield return (
            new TemplateRow("partner_viewer", "Partner Viewer", "Read-only access to partner portal"),
            [
                "partner:customer:view",
                "partner:billing:view",
                "partner:settings:view",
            ]);
    }

    private static string[] AllPermissions()
    {
        return
        [
            "contacts:conversation:handle", "contacts:conversation:transfer",
            "contacts:conversation:monitor", "contacts:conversation:barge",
            "contacts:conversation:whisper", "contacts:contact:view",
            "contacts:contact:edit", "contacts:contact:create",
            "queues:queue:view", "queues:queue:create",
            "queues:queue:edit", "queues:queue:delete",
            "queues:member:assign", "queues:member:view",
            "queues:member:delete", "queues:member:pause",
            "users:user:view", "users:user:create",
            "users:user:edit", "users:user:deactivate",
            "users:role:assign",
            "campaigns:campaign:view", "campaigns:campaign:create",
            "campaigns:campaign:edit", "campaigns:campaign:delete",
            "campaigns:campaign:execute", "campaigns:dnc:manage",
            "campaigns:callerid:manage", "campaigns:calendar:manage",
            "campaigns:route:manage", "campaigns:trunk:manage",
            "campaigns:dialer:configure",
            "reporting:realtime:view", "reporting:historical:view",
            "reporting:historical:export", "reporting:dashboard:edit",
            "quality:evaluation:view", "quality:evaluation:score",
            "quality:scorecard:manage",
            "recording:recording:play", "recording:recording:delete",
            "recording:recording:export",
            "routing:skill:view", "routing:skill:manage",
            "routing:flow:view", "routing:flow:edit",
            "analytics:cdr:view", "analytics:cdr:export",
            "analytics:interval:view", "analytics:alert:manage",
            "system:tenant:configure", "system:typification:configure",
            "system:integration:manage",
            "system:audit:view", "system:cluster:manage",
            "system:auth:configure",
            "agentassist:session:view", "agentassist:config:manage",
            "callanalytics:analysis:view", "callanalytics:config:manage",
            "platform:tenant:create", "platform:tenant:manage",
            "platform:tenant:suspend", "platform:tenant:delete",
            "platform:tenant:impersonate", "platform:server:manage",
            "platform:license:manage", "platform:cluster:manage",
            "features:agent-assist:manage",
            // R5.2 Phase 0 P0.9 — Security Admin + Audit + Impersonation + Retention
            // permissions seeded ahead of Phase A/B/C feature subagents that consume them.
            // Per ADR-0002 + ADR-0004 conventions; Web placeholder pages will tighten
            // their permission gates from existing fallbacks (system:auth:configure etc.)
            // to these new keys once the features land.
            "security.mfa.admin", "audit.read", "audit.export",
            "security.impersonation.manage",
            "retention.read", "retention.manage",
            "tenant.settings.write",
            // R5.4 S5.9 — JWT signing-key rotation. Gates POST /api/v1/management/security/jwt/rotate-key.
            "security.jwt.rotate",
            "partner:customer:view", "partner:customer:create",
            "partner:customer:manage", "partner:customer:delete",
            "partner:billing:view", "partner:billing:manage",
            "partner:settings:view", "partner:settings:manage",
            // P2b A5 — typification AI gating
            "typification:ai:configure",
            "typification:ai:autonomous",
            // ADR-0034 — supervisor correction of an autonomous disposition (also granted to
            // admin/system_admin via AllPermissions; supervisor gets it explicitly above).
            "typification:correct-autonomous",
            // c1 (credit-ledger-topups) — tenant admins may read their OWN AI-credit balance + ledger.
            // NOTE: billing:credits:grant is intentionally absent here (platform_admin-only); adding it
            // to AllPermissions() would leak the cross-tenant top-up grant to admin/system_admin.
            "billing:credits:read",
        ];
    }

    private static string[] AllPermissionsExcept(string[] excluded)
    {
        var excludeSet = new HashSet<string>(excluded);
        return AllPermissions().Where(p => !excludeSet.Contains(p)).ToArray();
    }

    private sealed record TemplateRow(string TemplateId, string Name, string Description);
}
