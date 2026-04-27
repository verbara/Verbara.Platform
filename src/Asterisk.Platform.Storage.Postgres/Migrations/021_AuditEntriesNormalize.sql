-- 021_AuditEntriesNormalize.sql
--
-- R5.3 Phase A Task A.1 — Normalize audit_entries schema (ADR-0006).
--
-- Promotes Category, Severity, ActorType, BeforeJson, AfterJson, IntegrityHash
-- from silent-loss-into-JSONB to first-class typed columns with CHECK
-- constraints + indexes. Closes the post-R5.2 deep-audit finding that
-- AuditEntry.cs declares 5 canonical fields whose values were lost (or buried
-- in the `details` JSONB blob without contract) at the writer boundary.
--
-- Audit Log Viewer (R5.2 PB.x ship) renders dropdown filters on `severity` and
-- `category`; without typed columns + indexes, the query plan today is a
-- sequential scan + JSONB extraction per row. New B-tree indexes on
-- (tenant_id, severity, occurred_at DESC) and (tenant_id, category,
-- occurred_at DESC) deliver the 10-100x query-perf lift documented in
-- ADR-0006 §"Consequences".
--
-- 3-stage atomic transaction (safe for deploys < ~100k audit rows; documented
-- batch pattern for > 10M rows lives in docs/operations/migrations.md, R5.3
-- Phase C output):
--
--   Stage 1: ADD COLUMN nullable for the 6 new columns.
--   Stage 2: backfill from `details` JSONB blob (best-effort) + RAISE NOTICE
--            audit of how many rows were backfilled vs defaulted.
--   Stage 3: NOT NULL + CHECK constraints + B-tree indexes.
--
-- Backwards compat: legacy `Metadata` dict continues to serialize into the
-- `details` JSONB blob; existing readers without the typed columns still see
-- all data via that blob.
--
-- Rollback (manual, post-incident only — drops indexes + columns):
--   DROP INDEX IF EXISTS idx_audit_severity;
--   DROP INDEX IF EXISTS idx_audit_category;
--   ALTER TABLE audit_entries
--     DROP COLUMN IF EXISTS category,
--     DROP COLUMN IF EXISTS severity,
--     DROP COLUMN IF EXISTS actor_type,
--     DROP COLUMN IF EXISTS before_json,
--     DROP COLUMN IF EXISTS after_json,
--     DROP COLUMN IF EXISTS integrity_hash;

-- NOTE: no explicit BEGIN/COMMIT — the C# DatabaseMigrationService wraps
-- each migration in its own transaction. Adding BEGIN/COMMIT here causes
-- double-wrap and Dapper sees "transaction is already completed" after the
-- nested COMMIT runs server-side.

-- Stage 1 — additive nullable columns. Idempotent across re-runs.
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS category       TEXT  NULL;
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS severity       TEXT  NULL;
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS actor_type     TEXT  NULL;
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS before_json    JSONB NULL;
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS after_json     JSONB NULL;
ALTER TABLE audit_entries ADD COLUMN IF NOT EXISTS integrity_hash TEXT  NULL;

-- Stage 2 — backfill from `details` JSONB blob, defaulting unset values.
-- Best-effort recovery of any historical rows that wrote canonical fields
-- into the blob via the legacy code path.
DO $$
DECLARE
    backfilled INTEGER := 0;
    defaulted  INTEGER := 0;
BEGIN
    UPDATE audit_entries
       SET category   = COALESCE(NULLIF(details->>'category', ''),   'config'),
           severity   = COALESCE(NULLIF(details->>'severity', ''),   'info'),
           actor_type = COALESCE(NULLIF(details->>'actor_type', ''), 'system')
     WHERE category IS NULL OR severity IS NULL OR actor_type IS NULL;

    GET DIAGNOSTICS backfilled = ROW_COUNT;

    SELECT COUNT(*) INTO defaulted
      FROM audit_entries
     WHERE category   = 'config'
       AND severity   = 'info'
       AND actor_type = 'system';

    RAISE NOTICE 'V021_AuditEntriesNormalize: backfilled % rows (% landed on full defaults)', backfilled, defaulted;
END $$;

-- Stage 3 — NOT NULL + CHECK constraints + indexes.
ALTER TABLE audit_entries ALTER COLUMN category   SET DEFAULT 'config';
ALTER TABLE audit_entries ALTER COLUMN severity   SET DEFAULT 'info';
ALTER TABLE audit_entries ALTER COLUMN actor_type SET DEFAULT 'system';

ALTER TABLE audit_entries ALTER COLUMN category   SET NOT NULL;
ALTER TABLE audit_entries ALTER COLUMN severity   SET NOT NULL;
ALTER TABLE audit_entries ALTER COLUMN actor_type SET NOT NULL;

-- CHECK constraints — defensive validation against typo'd or maliciously
-- crafted values reaching the audit trail. Drop-then-add for idempotency.
ALTER TABLE audit_entries DROP CONSTRAINT IF EXISTS audit_entries_severity_check;
ALTER TABLE audit_entries
    ADD CONSTRAINT audit_entries_severity_check
    CHECK (severity IN ('info', 'warn', 'warning', 'error', 'critical'));

ALTER TABLE audit_entries DROP CONSTRAINT IF EXISTS audit_entries_category_check;
ALTER TABLE audit_entries
    ADD CONSTRAINT audit_entries_category_check
    CHECK (category IN ('auth', 'billing', 'config', 'tenant', 'security',
                        'impersonation', 'retention', 'data', 'rbac',
                        'data_access', 'admin'));

ALTER TABLE audit_entries DROP CONSTRAINT IF EXISTS audit_entries_actor_type_check;
ALTER TABLE audit_entries
    ADD CONSTRAINT audit_entries_actor_type_check
    CHECK (actor_type IN ('user', 'system', 'impersonator', 'service-account', 'api_key'));

-- Filter indexes — match the Audit Log Viewer dropdown filter shape.
CREATE INDEX IF NOT EXISTS idx_audit_severity
    ON audit_entries (tenant_id, severity, occurred_at DESC);

CREATE INDEX IF NOT EXISTS idx_audit_category
    ON audit_entries (tenant_id, category, occurred_at DESC);
