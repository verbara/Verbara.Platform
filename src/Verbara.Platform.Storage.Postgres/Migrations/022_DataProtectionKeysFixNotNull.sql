-- 022_DataProtectionKeysFixNotNull.sql
--
-- R5.5 Phase 0L P0 finding #3 — relax NOT NULL on created_at / activates_at /
-- expires_at in `data_protection_keys`.
--
-- Root cause: V018 schema (ADR-0003) declared three timestamp columns
-- NOT NULL without DEFAULT. ASP.NET Core's stock
-- `EntityFrameworkCoreXmlRepository` (bound by `PersistKeysToDbContext<…>`)
-- only writes `(FriendlyName, Xml)` via `IXmlRepository.StoreElement`; it
-- never populates the three timestamps. Result: every JWT / cookie / MFA
-- secret persistence attempt failed with
--     "null value in column "created_at" of relation "data_protection_keys"
--      violates not-null constraint"
-- which surfaced at the boundary as `CryptographicException: An error
-- occurred while trying to encrypt the provided data` whenever
-- DataProtection tried to create a new keyring entry.
--
-- The bug went undetected through R5.2/R5.3/R5.4 because their CI runs use
-- the `--filter !~Postgres` test suite (no real DB) and the demo stack DB
-- already had a key persisted from a pre-V018 file-system fallback.
-- This is the first fresh Phase 0L bring-up on a clean Postgres volume.
--
-- Fix:
--   1. Drop NOT NULL on the three timestamp cols.
--   2. Add DEFAULT now() to created_at so rows still carry a server-side
--      insertion timestamp even with the stock IXmlRepository.
--   3. Replace the index on activates_at with a partial index so it stays
--      useful but doesn't bloat with NULL entries (steady state).
--   4. Backfill existing rows whose timestamps are NULL — none expected on
--      a normal upgrade path because the existing constraint would have
--      blocked any INSERT, but RAISE NOTICE the count for safety.
--
-- (V018 source has been updated to match — fresh deploys land directly on
-- this schema without needing V022 to apply.)
--
-- Rollback (restore NOT NULL — only safe after backfilling all rows):
--   ALTER TABLE data_protection_keys
--     ALTER COLUMN created_at DROP DEFAULT,
--     ALTER COLUMN created_at SET NOT NULL,
--     ALTER COLUMN activates_at SET NOT NULL,
--     ALTER COLUMN expires_at  SET NOT NULL;
--   DROP INDEX IF EXISTS idx_data_protection_keys_activates_at;
--   CREATE INDEX idx_data_protection_keys_activates_at
--     ON data_protection_keys (activates_at);

-- NOTE: no explicit BEGIN/COMMIT — the C# DatabaseMigrationService wraps
-- each migration in its own transaction.

ALTER TABLE data_protection_keys
    ALTER COLUMN created_at   DROP NOT NULL,
    ALTER COLUMN created_at   SET DEFAULT now(),
    ALTER COLUMN activates_at DROP NOT NULL,
    ALTER COLUMN expires_at   DROP NOT NULL;

-- Backfill: stamp created_at on any row that landed before this migration
-- (steady-state: 0 rows, because the broken constraint blocked all INSERTs).
DO $$
DECLARE
    backfilled INTEGER;
BEGIN
    UPDATE data_protection_keys
       SET created_at = now()
     WHERE created_at IS NULL;
    GET DIAGNOSTICS backfilled = ROW_COUNT;
    RAISE NOTICE 'V022_DataProtectionKeysFixNotNull: backfilled created_at on % rows', backfilled;
END $$;

DROP INDEX IF EXISTS idx_data_protection_keys_activates_at;

CREATE INDEX IF NOT EXISTS idx_data_protection_keys_activates_at
    ON data_protection_keys (activates_at)
    WHERE activates_at IS NOT NULL;
