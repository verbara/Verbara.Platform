-- 007_TenantsAndScheduledReports.sql
-- Tenants table for multi-tenant hierarchy, plus scheduled reports & executions.

-- ---------------------------------------------------------------------------
-- Tenants
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS tenants (
    tenant_id       TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    status          INTEGER NOT NULL DEFAULT 0,
    type            INTEGER NOT NULL DEFAULT 2,
    parent_tenant_id TEXT,
    options         JSONB NOT NULL DEFAULT '{}',
    metadata        JSONB,
    created_at      TIMESTAMPTZ NOT NULL,
    updated_at      TIMESTAMPTZ NOT NULL
);

-- At most one Platform (type=0) tenant can exist
CREATE UNIQUE INDEX IF NOT EXISTS ix_tenants_platform_unique
    ON tenants ((true)) WHERE type = 0;

CREATE INDEX IF NOT EXISTS ix_tenants_parent
    ON tenants (parent_tenant_id);

CREATE INDEX IF NOT EXISTS ix_tenants_status_active
    ON tenants (status) WHERE status = 0;

-- ---------------------------------------------------------------------------
-- Scheduled Reports
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS scheduled_reports (
    report_id       TEXT PRIMARY KEY,
    tenant_id       TEXT NOT NULL,
    name            TEXT NOT NULL,
    report_type     TEXT NOT NULL,
    schedule        TEXT NOT NULL,
    filters         TEXT,
    recipients      TEXT NOT NULL,
    format          TEXT NOT NULL,
    is_active       BOOLEAN NOT NULL DEFAULT true,
    created_by      TEXT NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL,
    updated_at      TIMESTAMPTZ NOT NULL,
    last_run_at     TIMESTAMPTZ,
    next_run_at     TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_scheduled_reports_tenant
    ON scheduled_reports (tenant_id);

CREATE INDEX IF NOT EXISTS ix_scheduled_reports_due
    ON scheduled_reports (next_run_at)
    WHERE is_active = true AND next_run_at IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Report Executions
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS report_executions (
    execution_id    TEXT PRIMARY KEY,
    report_id       TEXT NOT NULL,
    tenant_id       TEXT NOT NULL,
    started_at      TIMESTAMPTZ NOT NULL,
    completed_at    TIMESTAMPTZ,
    status          TEXT NOT NULL,
    format          TEXT NOT NULL,
    file_size_bytes BIGINT,
    error_message   TEXT,
    recipients_sent INTEGER
);

CREATE INDEX IF NOT EXISTS ix_report_executions_report
    ON report_executions (report_id, started_at DESC);
