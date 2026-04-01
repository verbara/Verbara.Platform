-- 005_GdprCompliance.sql
-- GDPR: purge log + tenant retention policies
-- NOTE: oidc_subject column on users table is added in Plan 30B migration.

CREATE TABLE IF NOT EXISTS purge_log (
    purge_id        VARCHAR(36) PRIMARY KEY,
    tenant_id       VARCHAR(36) NOT NULL,
    subject_type    VARCHAR(50) NOT NULL,
    subject_id      VARCHAR(100) NOT NULL,
    performed_by    VARCHAR(100) NOT NULL,
    reason          VARCHAR(500) NOT NULL,
    entities_deleted JSONB NOT NULL,
    purged_at       TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_purge_log_tenant ON purge_log(tenant_id);
CREATE INDEX IF NOT EXISTS ix_purge_log_purged_at ON purge_log(purged_at DESC);

CREATE TABLE IF NOT EXISTS tenant_retention_policies (
    tenant_id                    VARCHAR(36) PRIMARY KEY,
    conversation_retention_days  INTEGER,
    auth_event_retention_days    INTEGER,
    audit_retention_days         INTEGER,
    usage_record_retention_days  INTEGER
);
