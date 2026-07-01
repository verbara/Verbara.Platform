using Npgsql;
using Verbara.Sdk.Data.Npgsql;

namespace Verbara.Platform.Storage.Postgres.Seeds;

internal static class PermissionSeeder
{
    public static async Task SeedAsync(NpgsqlDataSource dataSource, CancellationToken ct = default)
    {
        const string sql =
            "INSERT INTO permissions (permission_id, category, resource, action, description, implies) " +
            "VALUES (@PermissionId, @Category, @Resource, @Action, @Description, @Implies) " +
            "ON CONFLICT (permission_id) DO NOTHING";

        // Dapper executed this INSERT once per element of the source sequence,
        // binding by property name. We reproduce that loop explicitly with the
        // facade's connection-less ExecuteAsync (each call opens/returns a pooled
        // connection from the data source).
        foreach (var p in GetPermissions())
        {
            await dataSource.ExecuteAsync(
                sql,
                ps =>
                {
                    ps.Add(new NpgsqlParameter("PermissionId", p.PermissionId));
                    ps.Add(new NpgsqlParameter("Category", p.Category));
                    ps.Add(new NpgsqlParameter("Resource", p.Resource));
                    ps.Add(new NpgsqlParameter("Action", p.Action));
                    ps.Add(new NpgsqlParameter("Description", p.Description));
                    ps.Add(new NpgsqlParameter("Implies", p.Implies));
                },
                ct);
        }
    }

    private static IEnumerable<PermissionRow> GetPermissions()
    {
        // ── contacts (8) ──
        yield return P("contacts:conversation:handle", "contacts", "conversation", "handle",
            "Handle inbound/outbound conversations");
        yield return P("contacts:conversation:transfer", "contacts", "conversation", "transfer",
            "Transfer conversations to other agents or queues");
        yield return P("contacts:conversation:monitor", "contacts", "conversation", "monitor",
            "Monitor (listen to) active conversations",
            ["contacts:conversation:handle"]);
        yield return P("contacts:conversation:barge", "contacts", "conversation", "barge",
            "Barge into active conversations (join call)",
            ["contacts:conversation:monitor", "contacts:conversation:handle"]);
        yield return P("contacts:conversation:whisper", "contacts", "conversation", "whisper",
            "Whisper to agent during active conversation",
            ["contacts:conversation:monitor", "contacts:conversation:handle"]);
        yield return P("contacts:contact:view", "contacts", "contact", "view",
            "View contact information");
        yield return P("contacts:contact:edit", "contacts", "contact", "edit",
            "Edit contact information",
            ["contacts:contact:view"]);
        yield return P("contacts:contact:create", "contacts", "contact", "create",
            "Create new contacts",
            ["contacts:contact:view"]);

        // ── queues (5) ──
        yield return P("queues:queue:view", "queues", "queue", "view",
            "View queue configuration and status");
        yield return P("queues:queue:create", "queues", "queue", "create",
            "Create new queues",
            ["queues:queue:view"]);
        yield return P("queues:queue:edit", "queues", "queue", "edit",
            "Edit queue configuration",
            ["queues:queue:view"]);
        yield return P("queues:queue:delete", "queues", "queue", "delete",
            "Delete queues",
            ["queues:queue:edit", "queues:queue:view"]);
        yield return P("queues:member:assign", "queues", "member", "assign",
            "Assign or remove queue members",
            ["queues:queue:view"]);
        // R5.1 Task I — RESTful queue-member endpoints with explicit verbs.
        yield return P("queues:member:view", "queues", "member", "view",
            "View queue-member assignments and pause state",
            ["queues:queue:view"]);
        yield return P("queues:member:delete", "queues", "member", "delete",
            "Remove queue-member assignments",
            ["queues:member:assign", "queues:queue:view"]);
        yield return P("queues:member:pause", "queues", "member", "pause",
            "Pause or resume queue members (AMI QueuePause)",
            ["queues:member:view", "queues:queue:view"]);

        // ── users (5) ──
        yield return P("users:user:view", "users", "user", "view",
            "View user profiles and status");
        yield return P("users:user:create", "users", "user", "create",
            "Create new users",
            ["users:user:view"]);
        yield return P("users:user:edit", "users", "user", "edit",
            "Edit user profiles",
            ["users:user:view"]);
        yield return P("users:user:deactivate", "users", "user", "deactivate",
            "Deactivate user accounts",
            ["users:user:view"]);
        yield return P("users:role:assign", "users", "role", "assign",
            "Assign roles to users",
            ["users:user:view"]);

        // ── campaigns (11) ──
        yield return P("campaigns:campaign:view", "campaigns", "campaign", "view",
            "View campaign configuration and status");
        yield return P("campaigns:campaign:create", "campaigns", "campaign", "create",
            "Create new campaigns",
            ["campaigns:campaign:view"]);
        yield return P("campaigns:campaign:edit", "campaigns", "campaign", "edit",
            "Edit campaign configuration",
            ["campaigns:campaign:view"]);
        yield return P("campaigns:campaign:delete", "campaigns", "campaign", "delete",
            "Delete campaigns",
            ["campaigns:campaign:edit", "campaigns:campaign:view"]);
        yield return P("campaigns:campaign:execute", "campaigns", "campaign", "execute",
            "Start, stop, pause campaigns",
            ["campaigns:campaign:view"]);
        yield return P("campaigns:dnc:manage", "campaigns", "dnc", "manage",
            "Manage Do-Not-Call lists",
            ["campaigns:campaign:view"]);
        yield return P("campaigns:callerid:manage", "campaigns", "callerid", "manage",
            "Manage Caller ID pools",
            ["campaigns:campaign:view"]);
        yield return P("campaigns:calendar:manage", "campaigns", "calendar", "manage",
            "Manage holiday calendars",
            ["campaigns:campaign:view"]);
        yield return P("campaigns:route:manage", "campaigns", "route", "manage",
            "Manage outbound routes",
            ["campaigns:campaign:view"]);
        yield return P("campaigns:trunk:manage", "campaigns", "trunk", "manage",
            "Manage SIP trunks",
            ["campaigns:campaign:view"]);
        yield return P("campaigns:dialer:configure", "campaigns", "dialer", "configure",
            "Configure dialer engine settings",
            ["campaigns:campaign:view"]);

        // ── reporting (4) ──
        yield return P("reporting:realtime:view", "reporting", "realtime", "view",
            "View real-time dashboards and metrics");
        yield return P("reporting:historical:view", "reporting", "historical", "view",
            "View historical reports");
        yield return P("reporting:historical:export", "reporting", "historical", "export",
            "Export historical reports",
            ["reporting:historical:view"]);
        yield return P("reporting:dashboard:edit", "reporting", "dashboard", "edit",
            "Create and edit custom dashboards",
            ["reporting:realtime:view", "reporting:historical:view"]);

        // ── quality (3) ──
        yield return P("quality:evaluation:view", "quality", "evaluation", "view",
            "View quality evaluations and scores");
        yield return P("quality:evaluation:score", "quality", "evaluation", "score",
            "Score quality evaluations",
            ["quality:evaluation:view"]);
        yield return P("quality:scorecard:manage", "quality", "scorecard", "manage",
            "Create and edit QA scorecards",
            ["quality:evaluation:view"]);

        // ── recording (3) ──
        yield return P("recording:recording:play", "recording", "recording", "play",
            "Play call recordings");
        yield return P("recording:recording:delete", "recording", "recording", "delete",
            "Delete call recordings",
            ["recording:recording:play"]);
        yield return P("recording:recording:export", "recording", "recording", "export",
            "Export/download call recordings",
            ["recording:recording:play"]);

        // ── routing (4) ──
        yield return P("routing:skill:view", "routing", "skill", "view",
            "View skill definitions and assignments");
        yield return P("routing:skill:manage", "routing", "skill", "manage",
            "Create, edit, delete skills and assign to agents",
            ["routing:skill:view"]);
        yield return P("routing:flow:view", "routing", "flow", "view",
            "View IVR/routing flows");
        yield return P("routing:flow:edit", "routing", "flow", "edit",
            "Edit IVR/routing flows",
            ["routing:flow:view"]);

        // ── analytics (4) ──
        yield return P("analytics:cdr:view", "analytics", "cdr", "view",
            "View call detail records");
        yield return P("analytics:cdr:export", "analytics", "cdr", "export",
            "Export call detail records",
            ["analytics:cdr:view"]);
        yield return P("analytics:interval:view", "analytics", "interval", "view",
            "View analytics interval snapshots");
        yield return P("analytics:alert:manage", "analytics", "alert", "manage",
            "Configure analytics alert rules",
            ["analytics:interval:view"]);

        // ── typification:ai (2) — P2b A5 ──
        yield return P("typification:ai:configure", "typification", "ai", "configure",
            "Enable AI classification, AutoFill, and configure thresholds for typification");
        yield return P("typification:ai:autonomous", "typification", "ai", "autonomous",
            "Allow autonomous (no-human) typification commits via AI classification");
        // ── typification:correct (1) — ADR-0034 supervisor correction ──
        yield return P("typification:correct-autonomous", "typification", "correct-autonomous", "correct",
            "Correct an autonomously stamped (AutoAi) typification disposition within the correction window");

        // ── system (6) ──
        yield return P("system:tenant:configure", "system", "tenant", "configure",
            "Configure tenant settings");
        yield return P("system:typification:configure", "system", "typification", "configure",
            "Configure typification (disposition) schemas and bindings");
        yield return P("system:integration:manage", "system", "integration", "manage",
            "Manage integrations (bots, webhooks, etc.)");
        yield return P("system:audit:view", "system", "audit", "view",
            "View audit logs");
        yield return P("system:cluster:manage", "system", "cluster", "manage",
            "Manage cluster nodes and configuration");
        yield return P("system:auth:configure", "system", "auth", "configure",
            "Configure authentication settings (MFA policy, OIDC, lockout, password policy)");

        // ── agentassist (2) ──
        yield return P("agentassist:session:view", "agentassist", "session", "view",
            "View agent assist sessions and transcripts");
        yield return P("agentassist:config:manage", "agentassist", "config", "manage",
            "Configure agent assist settings",
            ["agentassist:session:view"]);

        // ── callanalytics (2) ──
        yield return P("callanalytics:analysis:view", "callanalytics", "analysis", "view",
            "View call analysis results");
        yield return P("callanalytics:config:manage", "callanalytics", "config", "manage",
            "Configure call analytics settings",
            ["callanalytics:analysis:view"]);

        // ── partner (8) ──
        yield return P("partner:customer:view", "partner", "customer", "view",
            "View partner's child customer tenants");
        yield return P("partner:customer:create", "partner", "customer", "create",
            "Create customer tenants under this partner",
            ["partner:customer:view"]);
        yield return P("partner:customer:manage", "partner", "customer", "manage",
            "Edit settings, suspend, and activate child customers",
            ["partner:customer:view"]);
        yield return P("partner:customer:delete", "partner", "customer", "delete",
            "Delete child customer tenants (soft delete)",
            ["partner:customer:manage", "partner:customer:view"]);
        yield return P("partner:billing:view", "partner", "billing", "view",
            "View invoices, usage, and revenue for child customers");
        yield return P("partner:billing:manage", "partner", "billing", "manage",
            "Create rate cards, generate invoices, manage quotas",
            ["partner:billing:view"]);
        yield return P("partner:settings:view", "partner", "settings", "view",
            "View partner's own tenant settings");
        yield return P("partner:settings:manage", "partner", "settings", "manage",
            "Edit partner's own operational and auth settings",
            ["partner:settings:view"]);

        // ── platform (8) ──
        yield return P("platform:tenant:create", "platform", "tenant", "create",
            "Create new tenants (Customer or Partner)");
        yield return P("platform:tenant:manage", "platform", "tenant", "manage",
            "Edit tenant configuration, limits, and metadata");
        yield return P("platform:tenant:suspend", "platform", "tenant", "suspend",
            "Suspend or reactivate tenants",
            ["platform:tenant:manage"]);
        yield return P("platform:tenant:delete", "platform", "tenant", "delete",
            "Soft-delete tenants",
            ["platform:tenant:manage"]);
        yield return P("platform:tenant:impersonate", "platform", "tenant", "impersonate",
            "Operate in the context of a child tenant");
        yield return P("platform:server:manage", "platform", "server", "manage",
            "Manage Asterisk servers and monitor health");
        yield return P("platform:license:manage", "platform", "license", "manage",
            "View and activate platform licenses");
        yield return P("platform:cluster:manage", "platform", "cluster", "manage",
            "Manage cluster nodes, drain, and failover",
            ["system:cluster:manage"]);

        // ── features (platform-level runtime toggles, v1.9.4) ──
        yield return P("features:agent-assist:manage", "features", "agent-assist", "manage",
            "Enable/disable AgentAssist at runtime and rotate STT provider credentials");

        // ── security (R5.4 S5.9 — JWT signing-key rotation) ──
        yield return P("security.jwt.rotate", "security", "jwt", "rotate",
            "Rotate JWT signing keys (issues a new active key + keeps the prior key valid for the grace window)");

        // ── billing:credits (c1 credit-ledger-topups — ADR-0033 (c) addendum) ──
        yield return P("billing:credits:read", "billing", "credits", "read",
            "Read own AI-credit balance and ledger entries");
        yield return P("billing:credits:grant", "billing", "credits", "grant",
            "Mint AI-credit top-up grants for a tenant (operator)");
    }

    private static PermissionRow P(string id, string category, string resource, string action,
        string description, string[]? implies = null)
    {
        return new PermissionRow(id, category, resource, action, description, implies ?? Array.Empty<string>());
    }

    private sealed record PermissionRow(
        string PermissionId,
        string Category,
        string Resource,
        string Action,
        string Description,
        string[] Implies);
}
