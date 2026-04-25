using Dapper;
using Npgsql;

namespace Asterisk.Platform.Storage.Postgres.Seeds;

internal static class RoleTemplateSeeder
{
    public static async Task SeedAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        const string templateSql =
            "INSERT INTO role_templates (template_id, name, description, is_system, created_at) " +
            "VALUES (@TemplateId, @Name, @Description, true, now()) " +
            "ON CONFLICT (template_id) DO NOTHING";

        const string permSql =
            "INSERT INTO role_template_permissions (template_id, permission_id) " +
            "VALUES (@TemplateId, @PermissionId) " +
            "ON CONFLICT (template_id, permission_id) DO NOTHING";

        foreach (var (template, permissions) in GetTemplates())
        {
            await conn.ExecuteAsync(templateSql, template);
            foreach (var permissionId in permissions)
            {
                await conn.ExecuteAsync(permSql, new { template.TemplateId, PermissionId = permissionId });
            }
        }
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
        yield return (
            new TemplateRow("platform_admin", "Platform Admin", "Full platform administration including cross-tenant operations"),
            AllPermissions());

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
            "system:tenant:configure", "system:integration:manage",
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
            "partner:customer:view", "partner:customer:create",
            "partner:customer:manage", "partner:customer:delete",
            "partner:billing:view", "partner:billing:manage",
            "partner:settings:view", "partner:settings:manage",
        ];
    }

    private static string[] AllPermissionsExcept(string[] excluded)
    {
        var excludeSet = new HashSet<string>(excluded);
        return AllPermissions().Where(p => !excludeSet.Contains(p)).ToArray();
    }

    private sealed record TemplateRow(string TemplateId, string Name, string Description);
}
