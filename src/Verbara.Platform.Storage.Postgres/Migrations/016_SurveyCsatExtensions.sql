-- =============================================================================
-- Verbara.Platform — Survey/CSAT brownfield extension (016)
-- =============================================================================
-- csat-runner Phase A (Platform/ADR-0020, ADR-0022). Additive, back-compatible
-- storage substrate for customer-satisfaction capture as an extension of the
-- EXISTING Verbara.Platform.Surveys domain — NOT a parallel csat_responses table.
--
--  (a) survey_responses — 6 nullable CSAT columns (channel, queue_name, rating,
--      comment, captured_at, call_id) mapping 1:1 onto the frozen capture wire
--      shape (fixtures/csat-response-capture.v1.json). Two CHECK constraints and
--      two PARTIAL indexes (WHERE channel IS NOT NULL) that bound cost for the
--      non-CSAT NPS/Custom rows already in the table. All columns nullable so
--      pre-existing rows load unchanged (no backfill).
--  (b) csat_pending_dispatches (NEW) — SMS/email correlation lookup the Phase-D
--      correlator + Phase-C IMAP handler consume. Pro inserts on dispatch;
--      Platform reads + marks consumed_at. Partial index WHERE consumed_at IS NULL.
--  (c) queue_configs — 4 CSAT config columns (csat_enabled default false,
--      csat_channel, csat_prompt_id, csat_sampling_rate with 0..100 CHECK).
--      NOTE: the spec/tasks refer to this table as "queues"; the real table
--      created by 001_Baseline and bound by PostgresQueueStore is queue_configs.
--  (d) csat_templates (NEW) — per-tenant prompt store keyed (tenant_id,
--      template_id), channel in {voice,email,sms} (webchat uses i18n strings,
--      no per-tenant template).
--
-- Nullable ADD COLUMN in Postgres 11+ is metadata-only (no table rewrite); the
-- two new partial indexes scan the table — for large tenants apply the index DDL
-- separately with CREATE INDEX CONCURRENTLY (see the CSAT runbook). Because the
-- migration runner wraps each file in a transaction, this file uses plain
-- CREATE INDEX (CONCURRENTLY cannot run inside a transaction).
--
-- All idempotent (ADD COLUMN / CREATE {TABLE,INDEX} IF NOT EXISTS). All id columns
-- are TEXT (matches EntityId/TenantId usage across the platform), NOT uuid.
-- =============================================================================

-- (a) survey_responses: 6 nullable CSAT columns + CHECK constraints.
ALTER TABLE survey_responses
    ADD COLUMN IF NOT EXISTS channel     TEXT        NULL,
    ADD COLUMN IF NOT EXISTS queue_name  TEXT        NULL,
    ADD COLUMN IF NOT EXISTS rating      INTEGER     NULL,
    ADD COLUMN IF NOT EXISTS comment     TEXT        NULL,
    ADD COLUMN IF NOT EXISTS captured_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS call_id     TEXT        NULL;

-- CHECK constraints (both nullable-tolerant: a NULL passes a CHECK in Postgres).
-- Guarded with DO blocks so re-application does not error on the pre-existing
-- constraint (ADD CONSTRAINT has no IF NOT EXISTS in the supported PG versions).
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'chk_survey_responses_channel'
    ) THEN
        ALTER TABLE survey_responses
            ADD CONSTRAINT chk_survey_responses_channel
            CHECK (channel IN ('voice','webchat','email','sms'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'chk_survey_responses_rating'
    ) THEN
        ALTER TABLE survey_responses
            ADD CONSTRAINT chk_survey_responses_rating
            CHECK (rating BETWEEN 1 AND 5);
    END IF;
END $$;

-- Two PARTIAL indexes scoped to CSAT rows (WHERE channel IS NOT NULL) to bound
-- non-CSAT cost and serve the <2s supervisor-dashboard SLA.
CREATE INDEX IF NOT EXISTS idx_survey_resp_queue_captured
    ON survey_responses (tenant_id, queue_name, captured_at DESC)
    WHERE channel IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_survey_resp_agent_captured
    ON survey_responses (tenant_id, agent_id, captured_at DESC)
    WHERE channel IS NOT NULL;

-- (b) csat_pending_dispatches: SMS/email correlation lookup.
--     correlator = phone number (SMS) or token (email); 24h window via sent_at.
CREATE TABLE IF NOT EXISTS csat_pending_dispatches (
    dispatch_id     TEXT        NOT NULL,
    tenant_id       TEXT        NOT NULL,
    channel         TEXT        NOT NULL,
    correlator      TEXT        NOT NULL,
    survey_id       TEXT        NOT NULL,
    queue_name      TEXT        NULL,
    conversation_id TEXT        NULL,
    sent_at         TIMESTAMPTZ NOT NULL,
    expires_at      TIMESTAMPTZ NOT NULL,
    consumed_at     TIMESTAMPTZ NULL,
    PRIMARY KEY (tenant_id, dispatch_id),
    CONSTRAINT chk_csat_pending_dispatches_channel CHECK (channel IN ('email','sms'))
);

-- Open-dispatch lookup: (tenant, channel, correlator) among unconsumed rows,
-- most-recent-first for the collision "most-recent wins" rule.
CREATE INDEX IF NOT EXISTS idx_csat_pending_open
    ON csat_pending_dispatches (tenant_id, channel, correlator, sent_at DESC)
    WHERE consumed_at IS NULL;

-- (c0) queue_configs: repair pre-existing store/schema drift. PostgresQueueStore
--      has always read/written a wrap_up JSONB column (SELECT/INSERT + QueueRow),
--      but no migration ever created it (the 001_Baseline queue_configs DDL omits
--      it). Any real INSERT/UPDATE through the store fails 42703 against a migrated
--      DB. Added here (nullable, idempotent) so the Phase A queue round-trip works
--      and the store matches its schema. NOT a CSAT column, but a hard prerequisite.
ALTER TABLE queue_configs
    ADD COLUMN IF NOT EXISTS wrap_up JSONB NULL;

-- (c) queue_configs: 4 CSAT config columns (spec calls the table "queues").
ALTER TABLE queue_configs
    ADD COLUMN IF NOT EXISTS csat_enabled       BOOLEAN NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS csat_channel       TEXT    NULL,
    ADD COLUMN IF NOT EXISTS csat_prompt_id     TEXT    NULL,
    ADD COLUMN IF NOT EXISTS csat_sampling_rate INTEGER NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'chk_queue_configs_csat_sampling_rate'
    ) THEN
        ALTER TABLE queue_configs
            ADD CONSTRAINT chk_queue_configs_csat_sampling_rate
            CHECK (csat_sampling_rate IS NULL OR csat_sampling_rate BETWEEN 0 AND 100);
    END IF;
END $$;

-- (d) csat_templates: per-tenant prompt store keyed (tenant_id, template_id).
CREATE TABLE IF NOT EXISTS csat_templates (
    tenant_id   TEXT        NOT NULL,
    template_id TEXT        NOT NULL,
    channel     TEXT        NOT NULL,
    locale      TEXT        NOT NULL,
    subject     TEXT        NULL,
    body        TEXT        NOT NULL,
    is_default  BOOLEAN     NOT NULL DEFAULT false,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ NULL,
    PRIMARY KEY (tenant_id, template_id),
    CONSTRAINT chk_csat_templates_channel CHECK (channel IN ('voice','email','sms'))
);

-- Fallback-chain resolution lookup: (tenant, channel, locale).
CREATE INDEX IF NOT EXISTS idx_csat_templates_lookup
    ON csat_templates (tenant_id, channel, locale);
