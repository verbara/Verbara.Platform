-- =============================================================================
-- Verbara.Platform — Licensed-agent ledger, daily close and chain heads (019)
-- =============================================================================
-- licensed-agent-metering slice 2 (decision_ref verbara-meta/ADR-0020; design D5-D8).
--
-- license_agent_chain_heads  one row per chain: a tenant id, or '*deployment' for the
--                            deployment chain. Appends lock it (SELECT ... FOR UPDATE), which
--                            serialises a chain's appends and keeps `sequence` gap-free.
--                            day_zone is the IANA zone of the daily close, written once on the
--                            '*deployment' row when it is anchored; null on every tenant row.
-- license_agent_events       the append-only ledger (export: events[]).
-- license_agent_daily        the closed days and their corrections (export: daily[]).
--
-- One hash chain per tenant spans its events AND its daily rows (a shared `sequence`); the
-- `lac1` canonical form lives in Queues.Licensing.LicenseAgentChain. Ids only: no names,
-- e-mails or content.
--
-- Immutability: BEFORE UPDATE and BEFORE DELETE triggers reject every change to ledger and
-- daily rows, except a DELETE issued under `SET LOCAL verbara.license_purge = 'on'`, which
-- only the fixed 15-month purge sets. A head's day_zone can never change once written.
-- The owner controls the database and can disable the triggers: the chain deters rewriting,
-- it does not prove anything (ADR-0020 §6).
--
-- Additive only: no existing table changes, no backfill. The close worker anchors every
-- Customer chain (with its agent_baseline rows) on its first tick. Rollback is reverting the
-- image: the tables stay, inert. Do not drop them — they are billing evidence.
-- =============================================================================

CREATE TABLE IF NOT EXISTS license_agent_chain_heads (
    chain_key     TEXT        PRIMARY KEY,
    tenant_id     TEXT        NULL,
    head_sequence BIGINT      NOT NULL,
    head_hash     TEXT        NOT NULL,
    license_id    TEXT        NULL,
    day_zone      TEXT        NULL,
    anchored_at   TIMESTAMPTZ NOT NULL,
    updated_at    TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_license_agent_heads_day_zone
        CHECK (day_zone IS NULL OR chain_key = '*deployment')
);

CREATE TABLE IF NOT EXISTS license_agent_events (
    chain_key       TEXT        NOT NULL,
    sequence        BIGINT      NOT NULL,
    tenant_id       TEXT        NULL,
    event_id        UUID        NOT NULL,
    kind            TEXT        NOT NULL,
    occurred_at     TIMESTAMPTZ NOT NULL,
    agent_id        TEXT        NULL,
    user_id         TEXT        NULL,
    actor_user_id   TEXT        NULL,
    conversation_id TEXT        NULL,
    user_status     TEXT        NULL,
    counted         BOOLEAN     NULL,
    license_id      TEXT        NULL,
    prev_hash       TEXT        NOT NULL,
    row_hash        TEXT        NOT NULL,
    PRIMARY KEY (chain_key, sequence)
);

-- The close reads a chain's rows by time (day cut, late-event check) and by agent (start set).
CREATE INDEX IF NOT EXISTS idx_license_agent_events_occurred
    ON license_agent_events (chain_key, occurred_at);
CREATE INDEX IF NOT EXISTS idx_license_agent_events_agent
    ON license_agent_events (chain_key, agent_id, sequence);

CREATE TABLE IF NOT EXISTS license_agent_daily (
    chain_key               TEXT        NOT NULL,
    sequence                BIGINT      NOT NULL,
    tenant_id               TEXT        NULL,
    day                     DATE        NOT NULL,
    revision                INTEGER     NOT NULL,
    licensed_agents         INTEGER     NOT NULL,
    closed_at               TIMESTAMPTZ NOT NULL,
    closed_through_sequence BIGINT      NOT NULL,
    license_id              TEXT        NULL,
    prev_hash               TEXT        NOT NULL,
    row_hash                TEXT        NOT NULL,
    PRIMARY KEY (chain_key, sequence),
    -- Backs the per-chain lock: however many replicas or ticks try, one row per day and revision.
    CONSTRAINT ux_license_agent_daily_day_revision UNIQUE (chain_key, day, revision)
);

-- Append-only: an UPDATE always fails; a DELETE fails unless the retention purge set the flag.
CREATE OR REPLACE FUNCTION license_agent_reject_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' AND current_setting('verbara.license_purge', true) = 'on' THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION '% on % is not allowed: licensed-agent ledger rows are append-only (only the 15-month retention purge deletes)',
        TG_OP, TG_TABLE_NAME
        USING ERRCODE = 'restrict_violation';
END;
$$;

DROP TRIGGER IF EXISTS trg_license_agent_events_immutable ON license_agent_events;
CREATE TRIGGER trg_license_agent_events_immutable
    BEFORE UPDATE OR DELETE ON license_agent_events
    FOR EACH ROW EXECUTE FUNCTION license_agent_reject_mutation();

DROP TRIGGER IF EXISTS trg_license_agent_daily_immutable ON license_agent_daily;
CREATE TRIGGER trg_license_agent_daily_immutable
    BEFORE UPDATE OR DELETE ON license_agent_daily
    FOR EACH ROW EXECUTE FUNCTION license_agent_reject_mutation();

-- The day zone is fixed once per deployment (Q5): once a head carries one, no update changes it.
CREATE OR REPLACE FUNCTION license_agent_heads_keep_day_zone() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.day_zone IS NOT NULL AND NEW.day_zone IS DISTINCT FROM OLD.day_zone THEN
        RAISE EXCEPTION 'license_agent_chain_heads.day_zone of chain % is fixed at % and cannot change to %',
            OLD.chain_key, OLD.day_zone, COALESCE(NEW.day_zone, 'NULL')
            USING ERRCODE = 'restrict_violation';
    END IF;
    IF OLD.day_zone IS NULL AND NEW.day_zone IS NOT NULL THEN
        RAISE EXCEPTION 'license_agent_chain_heads.day_zone of chain % is written only when the chain is anchored',
            OLD.chain_key
            USING ERRCODE = 'restrict_violation';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_license_agent_heads_day_zone ON license_agent_chain_heads;
CREATE TRIGGER trg_license_agent_heads_day_zone
    BEFORE UPDATE ON license_agent_chain_heads
    FOR EACH ROW EXECUTE FUNCTION license_agent_heads_keep_day_zone();
