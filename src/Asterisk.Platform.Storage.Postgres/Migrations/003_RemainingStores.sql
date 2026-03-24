-- =============================================================================
-- Asterisk.Platform — Remaining Stores (003)
-- =============================================================================

-- ---------------------------------------------------------------------------
-- Teams
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS teams (
    team_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
    name TEXT NOT NULL, supervisor_id TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, team_id)
);

-- ---------------------------------------------------------------------------
-- Wrap-up records
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS wrap_up_records (
    tenant_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
    agent_id TEXT NOT NULL, disposition_id TEXT NOT NULL,
    notes TEXT, duration_ms BIGINT NOT NULL, completed_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, conversation_id)
);

-- ---------------------------------------------------------------------------
-- Dispositions
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS dispositions (
    disposition_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
    name TEXT NOT NULL, category INT NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT true, created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, disposition_id)
);

-- ---------------------------------------------------------------------------
-- Cases
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS cases (
    case_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
    case_number TEXT NOT NULL, subject TEXT NOT NULL,
    priority INT NOT NULL, status INT NOT NULL,
    contact_id TEXT NOT NULL, assigned_agent_id TEXT, sla_policy_id TEXT,
    conversation_ids JSONB NOT NULL DEFAULT '[]',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, case_id)
);

-- ---------------------------------------------------------------------------
-- Service accounts
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS service_accounts (
    account_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
    name TEXT NOT NULL, description TEXT NOT NULL DEFAULT '',
    scopes JSONB NOT NULL DEFAULT '[]',
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, account_id)
);

-- ---------------------------------------------------------------------------
-- Automation execution logs
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS automation_execution_logs (
    log_id TEXT NOT NULL PRIMARY KEY,
    rule_id TEXT NOT NULL, tenant_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
    trigger INT NOT NULL, conditions_matched BOOLEAN NOT NULL,
    actions_executed JSONB NOT NULL DEFAULT '[]', error TEXT,
    executed_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_auto_logs_conv ON automation_execution_logs (tenant_id, conversation_id, executed_at DESC);

-- ---------------------------------------------------------------------------
-- Surveys
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS surveys (
    survey_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
    name TEXT NOT NULL, type INT NOT NULL,
    questions JSONB NOT NULL DEFAULT '[]',
    is_active BOOLEAN NOT NULL DEFAULT true,
    PRIMARY KEY (tenant_id, survey_id)
);
CREATE INDEX IF NOT EXISTS idx_surveys_active ON surveys (tenant_id) WHERE is_active;

-- ---------------------------------------------------------------------------
-- Survey responses
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS survey_responses (
    response_id TEXT NOT NULL, survey_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
    contact_id TEXT NOT NULL, agent_id TEXT,
    answers JSONB NOT NULL DEFAULT '[]', submitted_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, response_id)
);
CREATE INDEX IF NOT EXISTS idx_survey_resp_conv ON survey_responses (tenant_id, conversation_id);
CREATE INDEX IF NOT EXISTS idx_survey_resp_survey ON survey_responses (tenant_id, survey_id);

-- ---------------------------------------------------------------------------
-- Audit entries
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS audit_entries (
    entry_id TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NOT NULL, action TEXT NOT NULL,
    entity_type TEXT NOT NULL, entity_id TEXT NOT NULL,
    performed_by TEXT, details JSONB, occurred_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_audit_entity ON audit_entries (tenant_id, entity_type, entity_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_audit_time ON audit_entries (tenant_id, occurred_at DESC);

-- ---------------------------------------------------------------------------
-- Media files
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS media_files (
    file_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
    file_name TEXT NOT NULL, content_type TEXT NOT NULL,
    size_bytes BIGINT NOT NULL, storage_path TEXT NOT NULL,
    conversation_id TEXT, uploaded_at TIMESTAMPTZ NOT NULL, uploaded_by TEXT,
    PRIMARY KEY (tenant_id, file_id)
);
CREATE INDEX IF NOT EXISTS idx_media_conv ON media_files (tenant_id, conversation_id);
