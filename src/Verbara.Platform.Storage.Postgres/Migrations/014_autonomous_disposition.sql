-- =============================================================================
-- Verbara.Platform — Autonomous typification disposition (014)
-- =============================================================================
-- Storage substrate for ADR-0034: AI disposition enrichment of the EXISTING
-- abandoned-wrap-up auto-close (ConversationTimeoutWorker). Phase A is additive
-- and idempotent; the worker / policy / endpoints land in later phases.
--
--  (a) typification_submissions — autonomous-actor + correction-state columns.
--      NO row_version column: the CAS commit is STATE-based (WHERE state='WrapUp'),
--      which is naturally idempotent (a re-attempt after success finds the
--      conversation no longer 'WrapUp').
--  (b) tenant_autonomous_disposition — per-tenant activation gate (a documented
--      CONTROLLER INSTRUCTION + config gate under the DPA, NOT data-subject consent;
--      ADR-0034 Decision 3). One row per tenant; soft-deleted via revoked_at.
--  (c) audit_entries.retain_until — per-record retention floor the AuditRetentionService
--      blanket purge will honour (ADR-0034 Decision 4); purge-logic change is Phase B.
--  (d) audit_entries.entity_id — drop NOT NULL so the Art.17 contact-linkage redaction
--      (RedactContactLinkageAsync SETs entity_id = NULL on autonomous-commit records while
--      retaining the decision-fact; ADR-0034 Decision 4) can land. The baseline declared
--      entity_id NOT NULL, which would make the redaction UPDATE throw 23502.
--
-- All idempotent (ADD COLUMN / CREATE TABLE IF NOT EXISTS / DROP NOT NULL). tenant_id is TEXT
-- (matches EntityId usage across the platform), NOT uuid.
-- =============================================================================

-- (a) typification_submissions: autonomous-actor + correction columns.
ALTER TABLE typification_submissions
    ADD COLUMN IF NOT EXISTS autonomous_actor_id      TEXT        NULL,
    ADD COLUMN IF NOT EXISTS correction_state         SMALLINT    NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS corrected_at             TIMESTAMPTZ NULL;

-- (b) tenant_autonomous_disposition: per-tenant activation gate (controller instruction).
CREATE TABLE IF NOT EXISTS tenant_autonomous_disposition (
    tenant_id           TEXT        NOT NULL,
    attested_by_user_id TEXT        NOT NULL,
    attested_at         TIMESTAMPTZ NOT NULL,
    revoked_at          TIMESTAMPTZ NULL,
    revoked_by_user_id  TEXT        NULL,
    PRIMARY KEY (tenant_id)
);

-- (c) audit_entries: per-record retention floor (purge floor honoured in Phase B).
ALTER TABLE audit_entries
    ADD COLUMN IF NOT EXISTS retain_until TIMESTAMPTZ NULL;

-- (d) audit_entries.entity_id: allow NULL for the Art.17 contact-linkage redaction (Decision 4).
ALTER TABLE audit_entries
    ALTER COLUMN entity_id DROP NOT NULL;
