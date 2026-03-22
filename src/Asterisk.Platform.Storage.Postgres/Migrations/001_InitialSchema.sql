-- =============================================================================
-- Asterisk.Platform — Initial Schema (001)
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
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    PRIMARY KEY (tenant_id, key_id)
);

CREATE INDEX IF NOT EXISTS idx_api_keys_hash ON api_keys (key_hash);

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

CREATE TABLE IF NOT EXISTS queues (
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
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ,
    created_by TEXT,
    updated_by TEXT,
    PRIMARY KEY (tenant_id, agent_id)
);

CREATE INDEX IF NOT EXISTS idx_agents_user ON agents (tenant_id, user_id);

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
