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

CREATE TABLE IF NOT EXISTS data_protection_keys (
    id                BIGSERIAL PRIMARY KEY,
    friendly_name     TEXT        NOT NULL,
    xml               TEXT        NOT NULL,
    created_at        TIMESTAMPTZ NOT NULL,
    activates_at      TIMESTAMPTZ NOT NULL,
    expires_at        TIMESTAMPTZ NOT NULL,
    is_revoked        BOOLEAN     NOT NULL DEFAULT FALSE,
    revocation_reason TEXT        NULL
);

CREATE INDEX IF NOT EXISTS idx_data_protection_keys_activates_at
    ON data_protection_keys (activates_at);
