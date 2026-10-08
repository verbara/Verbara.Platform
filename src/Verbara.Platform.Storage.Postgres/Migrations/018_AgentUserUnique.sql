-- =============================================================================
-- Verbara.Platform — one agent per user per tenant (018)
-- =============================================================================
-- licensed-agent-metering, slice 1 (decision_ref verbara-meta/ADR-0020, design D3).
-- A licensed-agent count is meaningful only over identities that are unique, so
-- each user owns at most one agent row per tenant from here on:
-- ux_agents_tenant_user replaces the non-unique idx_agents_user on the same
-- columns, and PostgresAgentStore turns a second agent for the same user into
-- HTTP 409.
--
-- FAIL-LOUD, NEVER DESTRUCTIVE. When duplicate (tenant_id, user_id) agent rows
-- already exist, this migration raises and changes nothing — the runner wraps the
-- file in a transaction, so the schema and every agent row stay exactly as they
-- were, and the API refuses to start until an operator resolves the duplicates.
-- It never deletes, merges or repoints an agent: the losing agent ids are still
-- referenced by conversation owners, queue memberships, queue_log/CDR interfaces
-- and Asterisk queue_members, which are the secondary signals an audit
-- reconciles against. The error lists every duplicated pair with each agent's id,
-- created_at and extension.
--
-- Before upgrading, run scripts/ops/agents-identity-report.sql (read-only): it
-- lists the duplicates, the references each losing agent would break, the
-- orphans and the orphaned Customer tenants. The manual dedup procedure is in
-- docs/operations/licensed-agent-metering-upgrade.md.
--
-- Agent rows whose user does not exist (orphans) do NOT block this migration;
-- the report lists them. No foreign key to users is added, for the same reason.
-- =============================================================================

DO $$
DECLARE
    duplicate_groups INTEGER;
    details TEXT;
BEGIN
    SELECT COUNT(*) INTO duplicate_groups
    FROM (
        SELECT tenant_id, user_id
        FROM agents
        GROUP BY tenant_id, user_id
        HAVING COUNT(*) > 1
    ) d;

    IF duplicate_groups > 0 THEN
        SELECT string_agg(pair, E'\n' ORDER BY pair) INTO details
        FROM (
            SELECT format(
                       '  tenant %s, user %s: %s',
                       a.tenant_id,
                       a.user_id,
                       string_agg(
                           format('agent %s (created_at %s, extension %s)',
                                  a.agent_id,
                                  to_char(a.created_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
                                  COALESCE(a.extension, 'none')),
                           '; ' ORDER BY a.created_at, a.agent_id)) AS pair
            FROM agents a
            JOIN (
                SELECT tenant_id, user_id
                FROM agents
                GROUP BY tenant_id, user_id
                HAVING COUNT(*) > 1
            ) d ON d.tenant_id = a.tenant_id AND d.user_id = a.user_id
            GROUP BY a.tenant_id, a.user_id
        ) pairs;

        -- Everything goes in MESSAGE: Npgsql redacts DETAIL unless the connection string sets
        -- Include Error Detail, and the startup log must name every pair whatever it says.
        RAISE EXCEPTION USING
            ERRCODE = 'unique_violation',
            MESSAGE = format(
                E'018_AgentUserUnique: %s user(s) own more than one agent in the same tenant; nothing was changed.\n'
                || E'%s\n'
                || 'Run scripts/ops/agents-identity-report.sql, then follow the dedup procedure in '
                || 'docs/operations/licensed-agent-metering-upgrade.md (keep the agent that has the extension, '
                || 'else the oldest created_at; repoint its references; delete the others) and restart.',
                duplicate_groups,
                details);
    END IF;
END
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_agents_tenant_user ON agents (tenant_id, user_id);

-- The unique index serves every lookup the old one did (same leading columns).
DROP INDEX IF EXISTS idx_agents_user;
