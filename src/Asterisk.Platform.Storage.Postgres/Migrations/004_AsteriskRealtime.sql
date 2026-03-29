-- =============================================================================
-- Asterisk.Platform — Agent SIP Fields + Queue Memberships (004)
-- =============================================================================
-- Asterisk Realtime tables (ps_endpoints, ps_auths, ps_aors, queues, etc.)
-- are now created by Pro.Realtime.Storage.Postgres via EnsureSchemaAsync.
-- This migration only contains Platform-owned schema changes.
-- =============================================================================

-- ---------------------------------------------------------------------------
-- Agent SIP fields (Platform-owned columns on Platform-owned table)
-- ---------------------------------------------------------------------------

ALTER TABLE agents ADD COLUMN IF NOT EXISTS extension VARCHAR(20);
ALTER TABLE agents ADD COLUMN IF NOT EXISTS sip_password VARCHAR(80);

-- ---------------------------------------------------------------------------
-- Queue memberships (Platform business-logic table)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS queue_memberships (
    tenant_id   TEXT NOT NULL,
    queue_id    TEXT NOT NULL,
    agent_id    TEXT NOT NULL,
    penalty     INTEGER NOT NULL DEFAULT 0,
    source      TEXT,
    is_excluded BOOLEAN NOT NULL DEFAULT FALSE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, queue_id, agent_id)
);
