-- =============================================================================
-- Verbara.Platform — Consolidated Baseline Schema (001)
-- =============================================================================
-- Pre-launch clean-break baseline. Folds the entire historical migration chain
-- (former 001_InitialSchema … 034_AuditCategoryVocabulary) into a single
-- definitive schema: every table appears once with all of its final columns,
-- indexes, and constraints. Pure data-normalization migrations (DML) are
-- dropped as no-ops on a fresh database; only their schema effects (columns,
-- defaults, CHECK constraints, NOT NULL/nullable transitions) are folded in.
--
-- Clean-break deltas vs the historical chain (flat-disposition domain removed):
--   * DROPPED: `dispositions` table
--   * DROPPED: `wrap_up_records` table
--   * DROPPED: `conversations.wrap_up` JSONB column
-- New (typification domain) — at the end of this file:
--   * ADDED: `typification_schemas`, `typification_bindings`, `typification_submissions`
--
-- The DatabaseMigrationService runner owns the `_migrations` ledger; this file
-- intentionally contains no bookkeeping.
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
    oidc_subject TEXT,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    PRIMARY KEY (tenant_id, user_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email ON users (tenant_id, lower(email));
CREATE INDEX IF NOT EXISTS ix_users_oidc_subject ON users (tenant_id, oidc_subject) WHERE oidc_subject IS NOT NULL;

CREATE TABLE IF NOT EXISTS api_keys (
    key_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    key_hash TEXT NOT NULL,
    name TEXT NOT NULL,
    scopes TEXT NOT NULL,
    rate_limit_per_minute INTEGER,
    is_revoked BOOLEAN NOT NULL DEFAULT false,
    user_id TEXT,
    key_type INTEGER NOT NULL DEFAULT 0,
    last_used_at TIMESTAMPTZ,
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
    user_agent TEXT,
    last_activity_at TIMESTAMPTZ NOT NULL DEFAULT now()
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
    impersonation_max_concurrent_sessions INTEGER NOT NULL DEFAULT 3,
    impersonation_auto_timeout_minutes INTEGER NOT NULL DEFAULT 240,
    ip_allowlist_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    agent_liveness_timeout_seconds INTEGER NOT NULL DEFAULT 60,
    pending_pause_timeout_minutes INTEGER NOT NULL DEFAULT 30,
    work_failover_grace_seconds INTEGER NOT NULL DEFAULT 30,
    voice_callback_grace_seconds INTEGER NOT NULL DEFAULT 25,
    max_voice_default INTEGER NOT NULL DEFAULT 1,
    max_chat_default INTEGER NOT NULL DEFAULT 3,
    max_email_default INTEGER NOT NULL DEFAULT 5,
    max_sms_default INTEGER NOT NULL DEFAULT 3,
    max_total_default INTEGER NOT NULL DEFAULT 5,
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
    voice_linked_id TEXT,
    queue_priority INTEGER NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL,
    closed_at TIMESTAMPTZ,
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    PRIMARY KEY (tenant_id, conversation_id)
);

CREATE INDEX IF NOT EXISTS idx_conversations_contact ON conversations (tenant_id, contact_id, state);
CREATE UNIQUE INDEX IF NOT EXISTS uq_conversations_voice_linked_id
    ON conversations (tenant_id, voice_linked_id)
    WHERE voice_linked_id IS NOT NULL;

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
    required_skills JSONB NOT NULL DEFAULT '[]',
    auto_answer_default BOOLEAN NOT NULL DEFAULT false,
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
    auto_answer BOOLEAN,
    pending_state INTEGER,
    pending_reason TEXT,
    pending_since TIMESTAMPTZ,
    offline_since TIMESTAMPTZ,
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
    allowed_channels TEXT[],
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
    default_flow_id TEXT,
    fallback_queue_id TEXT,
    confidence_threshold DOUBLE PRECISION NOT NULL DEFAULT 0.7,
    max_turns_before_handoff INTEGER NOT NULL DEFAULT 20,
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
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
    created_by TEXT, updated_by TEXT,
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
    performed_by TEXT, details JSONB, occurred_at TIMESTAMPTZ NOT NULL,
    impersonator_id TEXT,
    category TEXT NOT NULL DEFAULT 'config',
    severity TEXT NOT NULL DEFAULT 'info',
    actor_type TEXT NOT NULL DEFAULT 'system',
    before_json JSONB,
    after_json JSONB,
    integrity_hash TEXT,
    CONSTRAINT audit_entries_severity_check
        CHECK (severity IN ('info', 'warn', 'warning', 'error', 'critical')),
    CONSTRAINT audit_entries_category_check
        CHECK (category IN ('auth', 'billing', 'config', 'tenant', 'security',
                            'impersonation', 'retention', 'data', 'rbac',
                            'data_access', 'admin', 'conversations', 'queues',
                            'reports', 'operational', 'license')),
    CONSTRAINT audit_entries_actor_type_check
        CHECK (actor_type IN ('user', 'system', 'impersonator', 'service-account', 'api_key'))
);
CREATE INDEX IF NOT EXISTS idx_audit_entity ON audit_entries (tenant_id, entity_type, entity_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_audit_time ON audit_entries (tenant_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_audit_severity ON audit_entries (tenant_id, severity, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_audit_category ON audit_entries (tenant_id, category, occurred_at DESC);

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

-- ---------------------------------------------------------------------------
-- Billing — usage / quotas / rate cards / invoices / partner revenue / dunning
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS usage_records (
    record_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    usage_type SMALLINT NOT NULL,
    quantity NUMERIC(18,6) NOT NULL,
    unit SMALLINT NOT NULL,
    channel TEXT,
    reference_id TEXT,
    recorded_at TIMESTAMPTZ NOT NULL,
    metadata JSONB
);

CREATE INDEX IF NOT EXISTS idx_usage_tenant_period ON usage_records (tenant_id, recorded_at DESC);
CREATE INDEX IF NOT EXISTS idx_usage_tenant_type ON usage_records (tenant_id, usage_type, recorded_at DESC);

CREATE TABLE IF NOT EXISTS tenant_quotas (
    tenant_id TEXT PRIMARY KEY,
    max_concurrent_channels INT NOT NULL DEFAULT 100,
    max_active_campaigns INT NOT NULL DEFAULT 10,
    max_monthly_voice_minutes BIGINT,
    max_monthly_messages BIGINT,
    max_storage_bytes BIGINT,
    max_active_agents INT,
    quota_action SMALLINT NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS rate_cards (
    rate_card_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL,
    currency TEXT NOT NULL DEFAULT 'USD',
    effective_from TIMESTAMPTZ NOT NULL,
    effective_to TIMESTAMPTZ,
    rates JSONB NOT NULL,
    is_default BOOLEAN NOT NULL DEFAULT false,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_ratecard_tenant ON rate_cards (tenant_id, effective_from DESC);

CREATE TABLE IF NOT EXISTS invoices (
    invoice_id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    period_start TIMESTAMPTZ NOT NULL,
    period_end TIMESTAMPTZ NOT NULL,
    currency TEXT NOT NULL,
    line_items JSONB NOT NULL,
    subtotal NUMERIC(18,2) NOT NULL,
    tax NUMERIC(18,2) NOT NULL DEFAULT 0,
    total NUMERIC(18,2) NOT NULL,
    status SMALLINT NOT NULL DEFAULT 0,
    generated_at TIMESTAMPTZ NOT NULL,
    issued_at TIMESTAMPTZ,
    paid_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_invoice_tenant ON invoices (tenant_id, period_start DESC);

CREATE TABLE IF NOT EXISTS partner_revenue (
    revenue_id         TEXT PRIMARY KEY,
    partner_tenant_id  TEXT NOT NULL,
    customer_tenant_id TEXT NOT NULL,
    invoice_id         TEXT NOT NULL,
    gross_amount       NUMERIC(18,4) NOT NULL,
    platform_cost      NUMERIC(18,4) NOT NULL,
    partner_margin     NUMERIC(18,4) NOT NULL,
    period_start       TIMESTAMPTZ NOT NULL,
    period_end         TIMESTAMPTZ NOT NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_partner_revenue_partner_period
    ON partner_revenue (partner_tenant_id, period_start);

CREATE INDEX IF NOT EXISTS idx_partner_revenue_invoice
    ON partner_revenue (invoice_id);

CREATE TABLE IF NOT EXISTS dunning_records (
    dunning_id      VARCHAR(64) PRIMARY KEY,
    tenant_id       VARCHAR(64) NOT NULL,
    invoice_id      VARCHAR(64) NOT NULL,
    current_stage   VARCHAR(32) NOT NULL,
    started_at      TIMESTAMPTZ NOT NULL,
    escalated_at    TIMESTAMPTZ,
    resolved_at     TIMESTAMPTZ,
    is_paused       BOOLEAN NOT NULL DEFAULT FALSE,
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_dunning_records_tenant ON dunning_records(tenant_id) WHERE is_active = TRUE;
CREATE INDEX IF NOT EXISTS idx_dunning_records_invoice ON dunning_records(invoice_id) WHERE is_active = TRUE;

CREATE TABLE IF NOT EXISTS tenant_add_ons (
    tenant_id       VARCHAR(64) NOT NULL,
    feature         VARCHAR(64) NOT NULL,
    enabled_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, feature)
);

CREATE INDEX IF NOT EXISTS idx_tenant_add_ons_tenant ON tenant_add_ons(tenant_id);

CREATE TABLE IF NOT EXISTS agent_capacity (
    tenant_id   VARCHAR(64) NOT NULL,
    agent_id    VARCHAR(64) NOT NULL,
    voice_load  INTEGER NOT NULL DEFAULT 0,
    chat_load   INTEGER NOT NULL DEFAULT 0,
    email_load  INTEGER NOT NULL DEFAULT 0,
    sms_load    INTEGER NOT NULL DEFAULT 0,
    updated_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, agent_id)
);

-- ---------------------------------------------------------------------------
-- GDPR — purge log + retention policies
-- ---------------------------------------------------------------------------

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

-- ---------------------------------------------------------------------------
-- Outbound Webhooks (with circuit-breaker columns)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS webhook_subscriptions (
    subscription_id VARCHAR(36) PRIMARY KEY,
    tenant_id VARCHAR(36) NOT NULL,
    name VARCHAR(200) NOT NULL,
    endpoint_url VARCHAR(2000) NOT NULL,
    secret VARCHAR(64) NOT NULL,
    event_types JSONB NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT true,
    circuit_status VARCHAR(20) NOT NULL DEFAULT 'closed',
    circuit_failures INTEGER NOT NULL DEFAULT 0,
    circuit_opened_at TIMESTAMPTZ,
    circuit_next_probe_at TIMESTAMPTZ,
    circuit_probe_attempts INTEGER NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_webhook_subscriptions_tenant
    ON webhook_subscriptions(tenant_id);

CREATE TABLE IF NOT EXISTS webhook_deliveries (
    delivery_id VARCHAR(36) PRIMARY KEY,
    tenant_id VARCHAR(36) NOT NULL,
    subscription_id VARCHAR(36) NOT NULL REFERENCES webhook_subscriptions(subscription_id) ON DELETE CASCADE,
    event_type VARCHAR(100) NOT NULL,
    payload JSONB NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'Pending',
    attempts INTEGER NOT NULL DEFAULT 0,
    max_attempts INTEGER NOT NULL DEFAULT 8,
    next_retry_at TIMESTAMPTZ,
    last_response_code INTEGER,
    last_error TEXT,
    created_at TIMESTAMPTZ NOT NULL,
    delivered_at TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_pending
    ON webhook_deliveries(next_retry_at)
    WHERE status = 'Pending' AND next_retry_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_subscription
    ON webhook_deliveries(subscription_id);

CREATE INDEX IF NOT EXISTS ix_webhook_deliveries_dead_letter
    ON webhook_deliveries(tenant_id)
    WHERE status = 'DeadLetter';

-- ---------------------------------------------------------------------------
-- Tenants + Branding + Notifications
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

CREATE TABLE IF NOT EXISTS tenant_branding (
    tenant_id          TEXT PRIMARY KEY REFERENCES tenants(tenant_id),
    display_name       TEXT,
    logo_url           TEXT,
    favicon_url        TEXT,
    primary_color      TEXT,
    secondary_color    TEXT,
    accent_color       TEXT,
    locale             TEXT,
    timezone           TEXT,
    subdomain          TEXT,
    support_email      TEXT,
    support_url        TEXT,
    email_from_name    TEXT,
    email_from_address TEXT,
    created_at         TIMESTAMPTZ NOT NULL,
    updated_at         TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_tenant_branding_subdomain
    ON tenant_branding (subdomain) WHERE subdomain IS NOT NULL;

CREATE TABLE IF NOT EXISTS notifications (
    notification_id  TEXT PRIMARY KEY,
    tenant_id        TEXT NOT NULL,
    user_id          TEXT,
    category         INTEGER NOT NULL,
    severity         INTEGER NOT NULL,
    type             TEXT NOT NULL,
    title            TEXT NOT NULL,
    body             TEXT NOT NULL,
    action_url       TEXT,
    is_read          BOOLEAN NOT NULL DEFAULT false,
    created_at       TIMESTAMPTZ NOT NULL,
    read_at          TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS ix_notifications_user_unread
    ON notifications (tenant_id, user_id, created_at DESC)
    WHERE is_read = false;

CREATE INDEX IF NOT EXISTS ix_notifications_tenant_type_dedup
    ON notifications (tenant_id, type, created_at DESC);

-- ---------------------------------------------------------------------------
-- Scheduled Reports + Executions
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

-- ---------------------------------------------------------------------------
-- Mail (Microsoft OAuth token store)
-- ---------------------------------------------------------------------------

CREATE SCHEMA IF NOT EXISTS mail;

CREATE TABLE IF NOT EXISTS mail.oauth_tokens (
    id             TEXT PRIMARY KEY DEFAULT gen_random_uuid()::text,
    tenant_id      TEXT NOT NULL,
    user_id        TEXT NOT NULL,
    provider       TEXT NOT NULL DEFAULT 'microsoft',
    access_token   TEXT NOT NULL,
    refresh_token  TEXT NOT NULL,
    expires_at     TIMESTAMPTZ NOT NULL,
    scopes         TEXT NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, user_id, provider)
);

CREATE INDEX IF NOT EXISTS idx_oauth_tokens_expiring ON mail.oauth_tokens (expires_at);

-- ---------------------------------------------------------------------------
-- Canned Responses + Bot Analytics
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS canned_responses (
    response_id TEXT NOT NULL,
    tenant_id   TEXT NOT NULL,
    shortcut    TEXT NOT NULL,
    title       TEXT NOT NULL,
    body        TEXT NOT NULL,
    category    TEXT,
    tags        TEXT,
    created_by  TEXT NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at  TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, response_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_canned_responses_shortcut
    ON canned_responses (tenant_id, shortcut);

CREATE TABLE IF NOT EXISTS bot_analytics (
    id              BIGSERIAL PRIMARY KEY,
    tenant_id       TEXT NOT NULL,
    event_type      TEXT NOT NULL,
    bot_id          TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    turn_count      INTEGER NOT NULL DEFAULT 0,
    handoff_reason  TEXT,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_bot_analytics_tenant_date
    ON bot_analytics (tenant_id, created_at);

-- ---------------------------------------------------------------------------
-- DataProtection keys (ADR-0003) — nullable timestamps (folds 018 + 022)
-- ---------------------------------------------------------------------------

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

CREATE INDEX IF NOT EXISTS idx_data_protection_keys_activates_at
    ON data_protection_keys (activates_at)
    WHERE activates_at IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Tenant IP Allowlist
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS tenant_ip_allowlist (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id TEXT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    cidr CIDR NOT NULL,
    description TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_by_user_id TEXT,
    CONSTRAINT cidr_per_tenant_unique UNIQUE (tenant_id, cidr)
);

CREATE INDEX IF NOT EXISTS idx_tenant_ip_allowlist_tenant ON tenant_ip_allowlist(tenant_id);

-- ---------------------------------------------------------------------------
-- Voice inbound routing — DID routes
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS did_routes (
    route_id   TEXT        NOT NULL,
    tenant_id  TEXT        NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    did        TEXT        NOT NULL,
    queue_id   TEXT        NOT NULL,
    is_active  BOOLEAN     NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, route_id),
    CONSTRAINT did_per_tenant_unique UNIQUE (tenant_id, did)
);

CREATE INDEX IF NOT EXISTS idx_did_routes_did
    ON did_routes (tenant_id, did)
    WHERE is_active;

-- ---------------------------------------------------------------------------
-- Typification (replaces the removed flat-disposition domain)
-- ---------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS typification_schemas (
    tenant_id TEXT NOT NULL, schema_id TEXT NOT NULL,
    name TEXT NOT NULL, version INT NOT NULL DEFAULT 1,
    is_published BOOLEAN NOT NULL DEFAULT false,
    max_depth INT NOT NULL DEFAULT 5,
    definition JSONB NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ,
    PRIMARY KEY (tenant_id, schema_id, version)
);

CREATE TABLE IF NOT EXISTS typification_bindings (
    tenant_id TEXT NOT NULL, binding_id TEXT NOT NULL,
    scope TEXT NOT NULL, scope_ref TEXT,
    schema_id TEXT NOT NULL, subtree_root_node_id TEXT,
    priority INT NOT NULL DEFAULT 0,
    PRIMARY KEY (tenant_id, binding_id)
);
CREATE INDEX IF NOT EXISTS idx_typification_bindings_scope ON typification_bindings (tenant_id, scope, scope_ref);

CREATE TABLE IF NOT EXISTS typification_submissions (
    tenant_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
    agent_id TEXT NOT NULL, schema_id TEXT NOT NULL, schema_version INT NOT NULL,
    selected_node_path JSONB NOT NULL DEFAULT '[]', leaf_node_id TEXT NOT NULL,
    field_values JSONB NOT NULL DEFAULT '{}', notes TEXT,
    ai_suggested BOOLEAN NOT NULL DEFAULT false, ai_confidence DOUBLE PRECISION, ai_accepted BOOLEAN,
    source TEXT NOT NULL DEFAULT 'Manual', duration_ms BIGINT NOT NULL DEFAULT 0,
    completed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, conversation_id)
);
CREATE INDEX IF NOT EXISTS idx_typification_submissions_leaf ON typification_submissions (tenant_id, leaf_node_id, completed_at DESC);
CREATE INDEX IF NOT EXISTS idx_typification_submissions_completed ON typification_submissions (tenant_id, completed_at DESC);
