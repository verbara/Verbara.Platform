-- =============================================================================
-- Asterisk.Platform — Initial Schema (001)
-- =============================================================================
-- Consolidated Platform schema: Identity, Auth, RBAC, Conversations, Queues,
-- Channels, Flows, Bot, Automation, KnowledgeBase, Teams, Cases, Surveys,
-- Audit, Media. 34 tables total.
-- =============================================================================

-- ---------------------------------------------------------------------------
-- Identity
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS users (
    user_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    email TEXT NOT NULL,
    display_name TEXT NOT NULL,
    role INTEGER NOT NULL,
    status INTEGER NOT NULL DEFAULT 0,
    password_hash TEXT,
    mfa_enabled BOOLEAN NOT NULL DEFAULT false,
    mfa_secret TEXT,
    mfa_recovery_codes TEXT[],
    mfa_confirmed_at TIMESTAMPTZ,
    email_verified BOOLEAN NOT NULL DEFAULT false,
    failed_login_attempts INT NOT NULL DEFAULT 0,
    locked_until TIMESTAMPTZ,
    password_changed_at TIMESTAMPTZ,
    last_login_at TIMESTAMPTZ,
    auth_provider TEXT NOT NULL DEFAULT 'local',
    external_id TEXT,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    PRIMARY KEY (tenant_id, user_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email ON users (tenant_id, lower(email));

CREATE TABLE IF NOT EXISTS api_keys (
    key_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    key_hash TEXT NOT NULL,
    name TEXT NOT NULL,
    scopes TEXT NOT NULL,
    rate_limit_per_minute INTEGER,
    is_revoked BOOLEAN NOT NULL DEFAULT false,
    user_id TEXT,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    PRIMARY KEY (tenant_id, key_id)
);

CREATE INDEX IF NOT EXISTS idx_api_keys_hash ON api_keys (key_hash);

-- ---------------------------------------------------------------------------
-- Auth
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS refresh_tokens (
    token_id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    token_hash TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ,
    replaced_by TEXT,
    ip_address TEXT,
    user_agent TEXT
);
CREATE INDEX IF NOT EXISTS idx_refresh_tokens_hash ON refresh_tokens (token_hash) WHERE revoked_at IS NULL;
CREATE INDEX IF NOT EXISTS idx_refresh_tokens_user ON refresh_tokens (tenant_id, user_id);

CREATE TABLE IF NOT EXISTS auth_events (
    event_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    user_id TEXT,
    event_type TEXT NOT NULL,
    ip_address TEXT,
    user_agent TEXT,
    details JSONB,
    created_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_auth_events_tenant ON auth_events (tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_auth_events_user ON auth_events (tenant_id, user_id, created_at DESC);

CREATE TABLE IF NOT EXISTS tenant_auth_config (
    tenant_id TEXT PRIMARY KEY,
    mfa_policy TEXT NOT NULL DEFAULT 'optional',
    mfa_required_roles TEXT[] DEFAULT '{}',
    password_min_length INT NOT NULL DEFAULT 12,
    password_require_uppercase BOOLEAN NOT NULL DEFAULT true,
    password_require_number BOOLEAN NOT NULL DEFAULT true,
    password_require_special BOOLEAN NOT NULL DEFAULT false,
    lockout_threshold INT NOT NULL DEFAULT 5,
    lockout_duration_minutes INT NOT NULL DEFAULT 15,
    session_idle_timeout_minutes INT NOT NULL DEFAULT 30,
    session_absolute_timeout_hours INT NOT NULL DEFAULT 12,
    oidc_enabled BOOLEAN NOT NULL DEFAULT false,
    oidc_authority TEXT,
    oidc_client_id TEXT,
    oidc_client_secret TEXT,
    oidc_auto_create_users BOOLEAN NOT NULL DEFAULT true,
    oidc_default_role TEXT DEFAULT 'Agent',
    updated_at TIMESTAMPTZ
);

-- ---------------------------------------------------------------------------
-- RBAC
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS permissions (
    permission_id TEXT PRIMARY KEY,          -- "campaigns:campaign:delete"
    category TEXT NOT NULL,                  -- "campaigns"
    resource TEXT NOT NULL,                  -- "campaign"
    action TEXT NOT NULL,                    -- "delete"
    description TEXT NOT NULL,
    implies TEXT[]                           -- ["campaigns:campaign:edit", "campaigns:campaign:view"]
);

CREATE TABLE IF NOT EXISTS role_templates (
    template_id TEXT PRIMARY KEY,            -- "supervisor"
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    is_system BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS role_template_permissions (
    template_id TEXT NOT NULL REFERENCES role_templates(template_id) ON DELETE CASCADE,
    permission_id TEXT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
    PRIMARY KEY (template_id, permission_id)
);

CREATE TABLE IF NOT EXISTS tenant_roles (
    role_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    description TEXT,
    source_template_id TEXT,                 -- null = custom, "supervisor" = cloned
    is_default BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, role_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_tenant_roles_name
    ON tenant_roles (tenant_id, lower(name));

CREATE TABLE IF NOT EXISTS tenant_role_permissions (
    tenant_id TEXT NOT NULL,
    role_id TEXT NOT NULL,
    permission_id TEXT NOT NULL REFERENCES permissions(permission_id) ON DELETE CASCADE,
    PRIMARY KEY (tenant_id, role_id, permission_id),
    FOREIGN KEY (tenant_id, role_id) REFERENCES tenant_roles(tenant_id, role_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS user_roles (
    tenant_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    role_id TEXT NOT NULL,
    assigned_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    assigned_by TEXT,
    PRIMARY KEY (tenant_id, user_id, role_id),
    FOREIGN KEY (tenant_id, role_id) REFERENCES tenant_roles(tenant_id, role_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_user_roles_user
    ON user_roles (tenant_id, user_id);

CREATE INDEX IF NOT EXISTS idx_user_roles_role
    ON user_roles (tenant_id, role_id);

-- ---------------------------------------------------------------------------
-- Conversations
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS conversations (
    conversation_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    contact_id TEXT NOT NULL,
    channel INTEGER NOT NULL,
    state INTEGER NOT NULL,
    owner_kind INTEGER,
    owner_id TEXT,
    case_id TEXT,
    metadata JSONB NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL,
    closed_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    PRIMARY KEY (tenant_id, conversation_id)
);

CREATE INDEX IF NOT EXISTS idx_conversations_contact ON conversations (tenant_id, contact_id, state);

CREATE TABLE IF NOT EXISTS messages (
    message_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    direction INTEGER NOT NULL,
    channel INTEGER NOT NULL,
    sender_id TEXT,
    content JSONB NOT NULL,
    delivery_status INTEGER NOT NULL DEFAULT 0,
    external_message_id TEXT,
    created_at TIMESTAMPTZ NOT NULL,
    delivered_at TIMESTAMPTZ,
    read_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    PRIMARY KEY (tenant_id, message_id)
);

CREATE INDEX IF NOT EXISTS idx_messages_conversation ON messages (tenant_id, conversation_id, created_at);
CREATE INDEX IF NOT EXISTS idx_messages_external ON messages (tenant_id, external_message_id);

CREATE TABLE IF NOT EXISTS contacts (
    contact_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    first_name TEXT,
    last_name TEXT,
    company TEXT,
    segment TEXT,
    preferred_channel INTEGER,
    preferred_language TEXT,
    timezone TEXT,
    do_not_contact BOOLEAN NOT NULL DEFAULT false,
    addresses JSONB NOT NULL DEFAULT '[]',
    custom_fields JSONB NOT NULL DEFAULT '{}',
    channel_consent JSONB NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    PRIMARY KEY (tenant_id, contact_id)
);

-- ---------------------------------------------------------------------------
-- Queues
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS queue_configs (
    queue_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT true,
    max_waiting INTEGER,
    sla_targets JSONB,
    overflow_rule JSONB,
    hours JSONB,
    wrap_up JSONB,
    required_skills JSONB NOT NULL DEFAULT '[]',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    PRIMARY KEY (tenant_id, queue_id)
);

CREATE TABLE IF NOT EXISTS agents (
    agent_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    display_name TEXT NOT NULL,
    state INTEGER NOT NULL DEFAULT 0,
    capacity JSONB NOT NULL DEFAULT '{}',
    team_id TEXT,
    skills JSONB NOT NULL DEFAULT '[]',
    extension VARCHAR(20),
    sip_password VARCHAR(80),
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    PRIMARY KEY (tenant_id, agent_id)
);

CREATE INDEX IF NOT EXISTS idx_agents_user ON agents (tenant_id, user_id);

CREATE TABLE IF NOT EXISTS queue_memberships (
    tenant_id   TEXT NOT NULL,
    queue_id    TEXT NOT NULL,
    agent_id    TEXT NOT NULL,
    penalty     INTEGER NOT NULL DEFAULT 0,
    source      TEXT,
    is_excluded BOOLEAN NOT NULL DEFAULT FALSE,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, queue_id, agent_id)
);

-- ---------------------------------------------------------------------------
-- Channels
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS tenant_channel_configs (
    tenant_id TEXT NOT NULL,
    channel INTEGER NOT NULL,
    credentials JSONB NOT NULL DEFAULT '{}',
    is_active BOOLEAN NOT NULL DEFAULT true,
    PRIMARY KEY (tenant_id, channel)
);

-- ---------------------------------------------------------------------------
-- Flows
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS flow_definitions (
    flow_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    version INTEGER NOT NULL,
    is_published BOOLEAN NOT NULL DEFAULT false,
    entry_node_id TEXT NOT NULL,
    nodes JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, flow_id, version)
);

CREATE INDEX IF NOT EXISTS idx_flow_definitions_published ON flow_definitions (tenant_id, flow_id) WHERE is_published;

CREATE TABLE IF NOT EXISTS flow_executions (
    execution_id TEXT NOT NULL PRIMARY KEY,
    flow_id TEXT NOT NULL,
    flow_version INTEGER NOT NULL,
    tenant_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    current_node_id TEXT NOT NULL,
    status INTEGER NOT NULL DEFAULT 0,
    variables JSONB NOT NULL DEFAULT '{}',
    started_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ,
    step_count INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_flow_executions_conversation ON flow_executions (tenant_id, conversation_id, status);

-- ---------------------------------------------------------------------------
-- Bot
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS bot_configurations (
    bot_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    default_flow_id TEXT NOT NULL,
    fallback_queue_id TEXT,
    confidence_threshold DOUBLE PRECISION NOT NULL DEFAULT 0.7,
    max_turns_before_handoff INTEGER NOT NULL DEFAULT 20,
    is_active BOOLEAN NOT NULL DEFAULT true,
    PRIMARY KEY (tenant_id, bot_id)
);

-- ---------------------------------------------------------------------------
-- Automation
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS automation_rules (
    rule_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    trigger INTEGER NOT NULL,
    conditions JSONB NOT NULL DEFAULT '[]',
    actions JSONB NOT NULL DEFAULT '[]',
    is_active BOOLEAN NOT NULL DEFAULT true,
    priority INTEGER NOT NULL DEFAULT 100,
    max_executions_per_conversation INTEGER NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, rule_id)
);

CREATE INDEX IF NOT EXISTS idx_automation_rules_trigger ON automation_rules (tenant_id, trigger) WHERE is_active;

CREATE TABLE IF NOT EXISTS scheduled_timers (
    timer_id TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    callback_rule_id TEXT NOT NULL,
    fire_at TIMESTAMPTZ NOT NULL,
    is_fired BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_timers_fire ON scheduled_timers (fire_at) WHERE NOT is_fired;

CREATE TABLE IF NOT EXISTS automation_execution_logs (
    log_id TEXT NOT NULL PRIMARY KEY,
    rule_id TEXT NOT NULL, tenant_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
    trigger INT NOT NULL, conditions_matched BOOLEAN NOT NULL,
    actions_executed JSONB NOT NULL DEFAULT '[]', error TEXT,
    executed_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_auto_logs_conv ON automation_execution_logs (tenant_id, conversation_id, executed_at DESC);

-- ---------------------------------------------------------------------------
-- KnowledgeBase
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS articles (
    article_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    tags TEXT[] NOT NULL DEFAULT '{}',
    language TEXT,
    is_published BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, article_id)
);
CREATE INDEX IF NOT EXISTS idx_articles_published ON articles (tenant_id) WHERE is_published;

-- ---------------------------------------------------------------------------
-- Teams & Cases
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS teams (
    team_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
    name TEXT NOT NULL, supervisor_id TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, team_id)
);

CREATE TABLE IF NOT EXISTS cases (
    case_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
    case_number TEXT NOT NULL, subject TEXT NOT NULL,
    priority INT NOT NULL, status INT NOT NULL,
    contact_id TEXT NOT NULL, assigned_agent_id TEXT, sla_policy_id TEXT,
    conversation_ids JSONB NOT NULL DEFAULT '[]',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, case_id)
);

CREATE TABLE IF NOT EXISTS dispositions (
    disposition_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
    name TEXT NOT NULL, category INT NOT NULL DEFAULT 0,
    is_active BOOLEAN NOT NULL DEFAULT true, created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, disposition_id)
);

CREATE TABLE IF NOT EXISTS wrap_up_records (
    tenant_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
    agent_id TEXT NOT NULL, disposition_id TEXT NOT NULL,
    notes TEXT, duration_ms BIGINT NOT NULL, completed_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, conversation_id)
);

CREATE TABLE IF NOT EXISTS service_accounts (
    account_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
    name TEXT NOT NULL, description TEXT NOT NULL DEFAULT '',
    scopes JSONB NOT NULL DEFAULT '[]',
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, account_id)
);

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
-- Audit
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
-- Media
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS media_files (
    file_id TEXT NOT NULL, tenant_id TEXT NOT NULL,
    file_name TEXT NOT NULL, content_type TEXT NOT NULL,
    size_bytes BIGINT NOT NULL, storage_path TEXT NOT NULL,
    conversation_id TEXT, uploaded_at TIMESTAMPTZ NOT NULL, uploaded_by TEXT,
    PRIMARY KEY (tenant_id, file_id)
);
CREATE INDEX IF NOT EXISTS idx_media_conv ON media_files (tenant_id, conversation_id);
