-- Migration 018: DataProtection key persistence (ADR-0003)
--
-- Adds the `data_protection_keys` table required by the DB-backed
-- IXmlRepository wrapper that the Platform host registers as the default
-- DataProtection key store. See ADR-0003 for the rationale (file-system
-- writable mount is no longer required in production / multi-replica deploys).
--
-- NOTE: DatabaseMigrationService.ApplyMigrations wraps each migration in a
-- single transaction, so this file does NOT include BEGIN/COMMIT.
--
-- Rollback:
--   DROP INDEX IF EXISTS idx_data_protection_keys_activates_at;
--   DROP TABLE IF EXISTS data_protection_keys;

-- ASP.NET Core's stock EntityFrameworkCoreXmlRepository (the implementation
-- bound by PersistKeysToDbContext<PlatformDataProtectionDbContext>) only
-- writes the (FriendlyName, Xml) tuple via its IXmlRepository.StoreElement
-- contract. The three timestamp columns + revocation columns are extra
-- metadata kept for ops queries; they MUST be NULL-able (or default-backed)
-- so EF inserts don't trip the not-null constraint. created_at is
-- DEFAULT now() so the row carries a usable insertion timestamp even
-- without a custom IXmlRepository extracting from the XML blob.
-- (Activates_at + expires_at remain NULL until a future custom repo extracts
-- them from the keyring XML — see ADR-0003 for the planned extension.)
CREATE TABLE IF NOT EXISTS data_protection_keys (
    id                BIGSERIAL PRIMARY KEY,
    friendly_name     TEXT        NOT NULL,
    xml               TEXT        NOT NULL,
    created_at        TIMESTAMPTZ NULL DEFAULT now(),
    activates_at      TIMESTAMPTZ NULL,
    expires_at        TIMESTAMPTZ NULL,
    is_revoked        BOOLEAN     NOT NULL DEFAULT FALSE,
    revocation_reason TEXT        NULL
);

-- Partial index: only populated rows are eligible (avoids bloat from rows
-- with NULL activates_at, which is the steady state until a custom
-- IXmlRepository extracts the value from the XML blob).
CREATE INDEX IF NOT EXISTS idx_data_protection_keys_activates_at
    ON data_protection_keys (activates_at)
    WHERE activates_at IS NOT NULL;
