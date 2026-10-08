-- =============================================================================
-- agents-identity-report.sql — read-only pre-flight for migration 018_AgentUserUnique
-- =============================================================================
-- licensed-agent-metering (decision_ref verbara-meta/ADR-0020, design D3).
--
-- Run it BEFORE upgrading to the release that ships 018_AgentUserUnique, and again
-- after every manual fix, until section 1 returns no rows. It only reads: every
-- statement is a SELECT inside a READ ONLY transaction, so it cannot change data.
--
--   psql "$DATABASE_URL" -f scripts/ops/agents-identity-report.sql
--   docker compose exec -T postgres psql -U platform -d verbara < scripts/ops/agents-identity-report.sql
--
-- Sections:
--   1. Duplicate agents: users that own more than one agent row in the same tenant
--      (the rows that make 018 refuse to run), with the references each agent holds.
--   2. Orphan agents: agent rows whose user does not exist (they do not block 018,
--      but they are never routable and never counted).
--   3. Orphaned Customer tenants: type = Customer whose parent_tenant_id is null or
--      names a missing tenant (the licensed-agent daily close still counts them;
--      repair the parent).
--   4. Agents on non-Customer tenants: also run scripts/tenant-type-misplaced-data.sh.
--
-- The dedup procedure is in docs/operations/licensed-agent-metering-upgrade.md.
-- =============================================================================

BEGIN TRANSACTION READ ONLY;

\echo '== 1. Duplicate agents (tenant_id, user_id owns more than one agent) =='
\echo '   keep = the agent the dedup procedure keeps: the one with an extension, else the oldest created_at'
WITH dup AS (
    SELECT tenant_id, user_id
    FROM agents
    GROUP BY tenant_id, user_id
    HAVING COUNT(*) > 1
), ranked AS (
    SELECT a.tenant_id, a.user_id, a.agent_id, a.display_name, a.extension, a.created_at,
           ROW_NUMBER() OVER (
               PARTITION BY a.tenant_id, a.user_id
               ORDER BY (a.extension IS NULL OR a.extension = ''), a.created_at, a.agent_id) AS rank_in_user
    FROM agents a
    JOIN dup d ON d.tenant_id = a.tenant_id AND d.user_id = a.user_id
)
SELECT r.tenant_id,
       r.user_id,
       r.agent_id,
       r.display_name,
       COALESCE(r.extension, '') AS extension,
       r.created_at,
       CASE WHEN r.rank_in_user = 1 THEN 'keep' ELSE 'loser' END AS proposal,
       (SELECT COUNT(*) FROM queue_memberships qm
         WHERE qm.tenant_id = r.tenant_id AND qm.agent_id = r.agent_id) AS queue_memberships,
       (SELECT COUNT(*) FROM conversations c
         WHERE c.tenant_id = r.tenant_id AND c.owner_kind = 2 AND c.owner_id = r.agent_id) AS conversations_owned,
       (SELECT COUNT(*) FROM conversations c
         WHERE c.tenant_id = r.tenant_id AND c.owner_kind = 2 AND c.owner_id = r.agent_id
           AND c.state < 40) AS open_conversations_owned,
       (SELECT COUNT(*) FROM agent_capacity ac
         WHERE ac.tenant_id = r.tenant_id AND ac.agent_id = r.agent_id) AS capacity_rows,
       'PJSIP/' || r.tenant_id || '-agent-' || r.agent_id AS asterisk_interface
FROM ranked r
ORDER BY r.tenant_id, r.user_id, r.rank_in_user;

\echo '== 2. Orphan agents (user_id names no user in the tenant) =='
SELECT a.tenant_id, a.agent_id, a.user_id, a.display_name, COALESCE(a.extension, '') AS extension, a.created_at,
       (SELECT COUNT(*) FROM queue_memberships qm
         WHERE qm.tenant_id = a.tenant_id AND qm.agent_id = a.agent_id) AS queue_memberships,
       (SELECT COUNT(*) FROM conversations c
         WHERE c.tenant_id = a.tenant_id AND c.owner_kind = 2 AND c.owner_id = a.agent_id) AS conversations_owned
FROM agents a
WHERE NOT EXISTS (SELECT 1 FROM users u WHERE u.tenant_id = a.tenant_id AND u.user_id = a.user_id)
ORDER BY a.tenant_id, a.agent_id;

\echo '== 3. Orphaned Customer tenants (parent_tenant_id null or missing) =='
SELECT t.tenant_id, t.name, t.status, COALESCE(t.parent_tenant_id, '') AS parent_tenant_id,
       CASE WHEN t.parent_tenant_id IS NULL THEN 'parent is null' ELSE 'parent does not exist' END AS problem,
       (SELECT COUNT(*) FROM agents a WHERE a.tenant_id = t.tenant_id) AS agents
FROM tenants t
WHERE t.type = 2
  AND (t.parent_tenant_id IS NULL
       OR NOT EXISTS (SELECT 1 FROM tenants p WHERE p.tenant_id = t.parent_tenant_id))
ORDER BY t.tenant_id;

\echo '== 4. Agents on non-Customer tenants (also run scripts/tenant-type-misplaced-data.sh) =='
SELECT a.tenant_id, t.type AS tenant_type, a.agent_id, a.user_id, a.display_name
FROM agents a
JOIN tenants t ON t.tenant_id = a.tenant_id
WHERE t.type <> 2
ORDER BY a.tenant_id, a.agent_id;

ROLLBACK;
