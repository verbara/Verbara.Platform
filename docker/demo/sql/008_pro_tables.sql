-- =============================================================================
-- 008_pro_tables.sql — Pro Package Tables for Demo Environment
-- =============================================================================
-- Runs during Postgres init (docker-entrypoint-initdb.d) AFTER Platform
-- migration (001) and BEFORE demo seed data (010).
--
-- Sources (applied in order, fully idempotent):
--   Pro.Dialer.Storage.Postgres  — InitialSchema + V2–V6 migrations
--   Pro.Realtime.Storage.Postgres — RealtimeSchema
--   Pro.EventStore.Postgres      — EventStoreSchema + V5 + V7
--   Pro.Analytics.Storage.Postgres — AnalyticsSchema
--   Pro.AgentAssist.Storage.Postgres — 001_AgentAssist_Schema
--   Pro.CallAnalytics.Storage.Postgres — 001_CallAnalytics_Schema
-- =============================================================================


-- =============================================================================
-- Pro.Dialer — InitialSchema (V1)
-- =============================================================================

CREATE TABLE IF NOT EXISTS dialer_settings (
    id                              BIGSERIAL PRIMARY KEY,
    max_global_channels             INT NOT NULL DEFAULT 20,
    default_ring_timeout_seconds    INT NOT NULL DEFAULT 20,
    campaign_poll_interval_seconds  INT NOT NULL DEFAULT 30,
    max_concurrent_campaigns        INT NOT NULL DEFAULT 30,
    blend_mode_enabled              BOOLEAN NOT NULL DEFAULT FALSE,
    jitter_min_ms                   INT NOT NULL DEFAULT 100,
    jitter_max_ms                   INT NOT NULL DEFAULT 800,
    aht_cache_duration_seconds      INT NOT NULL DEFAULT 120,
    scheduled_callback_poll_seconds INT NOT NULL DEFAULT 40
);

INSERT INTO dialer_settings (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS campaigns (
    id                              BIGSERIAL PRIMARY KEY,
    name                            TEXT NOT NULL,
    description                     TEXT,
    external_id                     TEXT,
    status                          INT NOT NULL DEFAULT 0,
    created_at                      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    started_at                      TIMESTAMPTZ,
    completed_at                    TIMESTAMPTZ,
    starts_at                       TIMESTAMPTZ,
    ends_at                         TIMESTAMPTZ,
    mode                            INT NOT NULL DEFAULT 2,
    max_concurrent_calls            INT NOT NULL DEFAULT 1,
    overdial_factor                 INT NOT NULL DEFAULT 0,
    dynamic_channel_allocation      BOOLEAN NOT NULL DEFAULT FALSE,
    overdial_on_busy                BOOLEAN NOT NULL DEFAULT FALSE,
    overdial_cooldown_seconds       INT,
    target_abandon_rate             DOUBLE PRECISION NOT NULL DEFAULT 0.03,
    max_overdial_ratio              INT NOT NULL DEFAULT 3,
    inter_batch_min_wait_seconds    INT NOT NULL DEFAULT 3,
    inter_batch_max_wait_seconds    INT NOT NULL DEFAULT 10,
    aht_offset_seconds              INT NOT NULL DEFAULT 5,
    power_ratio                     DOUBLE PRECISION NOT NULL DEFAULT 1.0,
    ring_timeout_seconds            INT NOT NULL DEFAULT 20,
    max_attempts_per_contact        INT NOT NULL DEFAULT 3,
    immediate_retries_per_phone     INT NOT NULL DEFAULT 2,
    immediate_retry_delay_ms        INT NOT NULL DEFAULT 2000,
    default_retry_delay_minutes     INT,
    amd_enabled                     BOOLEAN NOT NULL DEFAULT FALSE,
    amd_action                      INT NOT NULL DEFAULT 0,
    amd_action_value                TEXT,
    caller_id_strategy              INT NOT NULL DEFAULT 0,
    caller_id_value                 TEXT,
    caller_id_pool_id               BIGINT,
    target_queue_name               TEXT,
    default_trunk_id                BIGINT,
    wrap_up_time_seconds            INT NOT NULL DEFAULT 30,
    blend_enabled                   BOOLEAN NOT NULL DEFAULT FALSE,
    blend_prioritize_inbound        BOOLEAN NOT NULL DEFAULT FALSE,
    blend_inbound_threshold         INT NOT NULL DEFAULT 0,
    human_answer_action             INT NOT NULL DEFAULT 1,
    human_answer_action_value       TEXT,
    default_answer_action           INT NOT NULL DEFAULT 1,
    default_answer_action_value     TEXT,
    reinject_abandoned_calls        BOOLEAN NOT NULL DEFAULT FALSE,
    reinject_rejected_calls         BOOLEAN NOT NULL DEFAULT FALSE,
    reinject_contact_list_id        BIGINT,
    reinject_callback_prefix        TEXT DEFAULT '888',
    run_on_holidays                 BOOLEAN NOT NULL DEFAULT FALSE,
    holiday_calendar_id             BIGINT,
    dnc_list_id                     BIGINT,
    check_global_dnc                BOOLEAN NOT NULL DEFAULT TRUE,
    max_calls_per_contact_per_day   INT,
    contact_timezone                TEXT,
    max_penetration_percent         DOUBLE PRECISION,
    contact_ttl_days                INT,
    dialplan_context                TEXT NOT NULL DEFAULT 'default',
    dispositions_enabled            BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE INDEX IF NOT EXISTS ix_campaigns_status ON campaigns (status);

CREATE TABLE IF NOT EXISTS contact_lists (
    id          BIGSERIAL PRIMARY KEY,
    campaign_id BIGINT NOT NULL REFERENCES campaigns (id),
    name        TEXT NOT NULL,
    status      INT NOT NULL DEFAULT 0,
    source      INT NOT NULL DEFAULT 0,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_contact_lists_campaign ON contact_lists (campaign_id);

CREATE TABLE IF NOT EXISTS dialer_contacts (
    id                       BIGSERIAL PRIMARY KEY,
    contact_list_id          BIGINT NOT NULL REFERENCES contact_lists (id),
    external_id              TEXT,
    first_name               TEXT,
    last_name                TEXT,
    status                   INT NOT NULL DEFAULT 0,
    attempt_number           INT NOT NULL DEFAULT 0,
    priority                 INT NOT NULL DEFAULT 0,
    retry_enabled            BOOLEAN NOT NULL DEFAULT TRUE,
    earliest_next_attempt_at TIMESTAMPTZ,
    assigned_agent_id        TEXT,
    created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at               TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_contacts_list_status_priority
    ON dialer_contacts (contact_list_id, status, priority DESC, id);

CREATE TABLE IF NOT EXISTS contact_phones (
    id           BIGSERIAL PRIMARY KEY,
    contact_id   BIGINT NOT NULL REFERENCES dialer_contacts (id),
    phone_number TEXT NOT NULL,
    phone_type   INT NOT NULL DEFAULT 0,
    status       INT NOT NULL DEFAULT 0,
    priority     INT NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_contact_phones_contact ON contact_phones (contact_id);

CREATE TABLE IF NOT EXISTS call_attempts (
    id                  BIGSERIAL PRIMARY KEY,
    campaign_id         BIGINT NOT NULL,
    contact_id          BIGINT NOT NULL REFERENCES dialer_contacts (id),
    contact_list_id     BIGINT NOT NULL,
    contact_phone_id    BIGINT NOT NULL REFERENCES contact_phones (id),
    phone_number        TEXT NOT NULL,
    channel             TEXT,
    call_unique_id      TEXT,
    attempt_number      INT NOT NULL DEFAULT 0,
    result              INT NOT NULL DEFAULT 0,
    raw_response_code   TEXT,
    raw_response_text   TEXT,
    trunk_id            BIGINT,
    trunk_name          TEXT,
    started_at          TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    answered_at         TIMESTAMPTZ,
    ended_at            TIMESTAMPTZ,
    duration_seconds    DOUBLE PRECISION,
    talk_time_seconds   DOUBLE PRECISION,
    disposition_id      BIGINT,
    agent_id            TEXT,
    agent_comment       TEXT,
    external_contact_id TEXT
);

CREATE INDEX IF NOT EXISTS ix_call_attempts_contact  ON call_attempts (contact_id);
CREATE INDEX IF NOT EXISTS ix_call_attempts_campaign ON call_attempts (campaign_id);

CREATE TABLE IF NOT EXISTS dnc_lists (
    id          BIGSERIAL PRIMARY KEY,
    name        TEXT NOT NULL,
    scope       INT NOT NULL DEFAULT 0,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS dnc_entries (
    id           BIGSERIAL PRIMARY KEY,
    dnc_list_id  BIGINT NOT NULL REFERENCES dnc_lists (id),
    phone_number TEXT NOT NULL,
    expires_at   TIMESTAMPTZ,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS uix_dnc_entries_list_phone
    ON dnc_entries (dnc_list_id, phone_number);

CREATE INDEX IF NOT EXISTS ix_dnc_entries_phone ON dnc_entries (phone_number);

CREATE TABLE IF NOT EXISTS trunks (
    id           BIGSERIAL PRIMARY KEY,
    name         TEXT NOT NULL,
    display_name TEXT,
    type         INT NOT NULL DEFAULT 3,
    is_active    BOOLEAN NOT NULL DEFAULT TRUE,
    max_channels INT NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS outbound_routes (
    id           BIGSERIAL PRIMARY KEY,
    trunk_id     BIGINT NOT NULL REFERENCES trunks (id),
    pattern      TEXT NOT NULL,
    pattern_type INT NOT NULL DEFAULT 0,
    priority     INT NOT NULL DEFAULT 0,
    campaign_id  BIGINT
);

CREATE INDEX IF NOT EXISTS ix_outbound_routes_priority ON outbound_routes (priority);

CREATE TABLE IF NOT EXISTS scheduled_callbacks (
    id           BIGSERIAL PRIMARY KEY,
    campaign_id  BIGINT NOT NULL,
    contact_id   BIGINT NOT NULL,
    scheduled_at TIMESTAMPTZ NOT NULL,
    agent_id     TEXT,
    executed     BOOLEAN NOT NULL DEFAULT FALSE,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_scheduled_callbacks_pending
    ON scheduled_callbacks (scheduled_at) WHERE executed = FALSE;

CREATE TABLE IF NOT EXISTS holiday_calendars (
    id         BIGSERIAL PRIMARY KEY,
    name       TEXT NOT NULL,
    timezone   TEXT NOT NULL DEFAULT 'UTC',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS holidays (
    id                  BIGSERIAL PRIMARY KEY,
    holiday_calendar_id BIGINT NOT NULL REFERENCES holiday_calendars (id),
    date                DATE NOT NULL,
    name                TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS uix_holidays_calendar_date
    ON holidays (holiday_calendar_id, date);

CREATE TABLE IF NOT EXISTS caller_id_pools (
    id         BIGSERIAL PRIMARY KEY,
    name       TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS caller_id_entries (
    id           BIGSERIAL PRIMARY KEY,
    pool_id      BIGINT NOT NULL REFERENCES caller_id_pools (id),
    phone_number TEXT NOT NULL,
    is_active    BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE INDEX IF NOT EXISTS ix_caller_id_entries_pool ON caller_id_entries (pool_id);


-- =============================================================================
-- Pro.Dialer — V2: Add tenant_id columns
-- =============================================================================

ALTER TABLE dialer_settings    ADD COLUMN IF NOT EXISTS tenant_id TEXT NOT NULL DEFAULT '';
ALTER TABLE campaigns          ADD COLUMN IF NOT EXISTS tenant_id TEXT NOT NULL DEFAULT '';
ALTER TABLE contact_lists      ADD COLUMN IF NOT EXISTS tenant_id TEXT NOT NULL DEFAULT '';
ALTER TABLE dialer_contacts    ADD COLUMN IF NOT EXISTS tenant_id TEXT NOT NULL DEFAULT '';
ALTER TABLE call_attempts      ADD COLUMN IF NOT EXISTS tenant_id TEXT NOT NULL DEFAULT '';
ALTER TABLE dnc_lists          ADD COLUMN IF NOT EXISTS tenant_id TEXT NOT NULL DEFAULT '';
ALTER TABLE scheduled_callbacks ADD COLUMN IF NOT EXISTS tenant_id TEXT NOT NULL DEFAULT '';
ALTER TABLE holiday_calendars  ADD COLUMN IF NOT EXISTS tenant_id TEXT NOT NULL DEFAULT '';
ALTER TABLE trunks             ADD COLUMN IF NOT EXISTS tenant_id TEXT NOT NULL DEFAULT '';
ALTER TABLE caller_id_pools    ADD COLUMN IF NOT EXISTS tenant_id TEXT NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS ix_campaigns_tenant_status  ON campaigns    (tenant_id, status);
CREATE INDEX IF NOT EXISTS ix_contacts_tenant          ON dialer_contacts (tenant_id);
CREATE INDEX IF NOT EXISTS ix_call_attempts_tenant     ON call_attempts (tenant_id, campaign_id);


-- =============================================================================
-- Pro.Dialer — V3: disposition_codes, campaign_schedule_days, retry_rules
-- =============================================================================

CREATE TABLE IF NOT EXISTS disposition_codes (
    id                  BIGSERIAL PRIMARY KEY,
    tenant_id           TEXT NOT NULL DEFAULT '',
    campaign_id         BIGINT REFERENCES campaigns (id),
    code                TEXT NOT NULL,
    label               TEXT NOT NULL,
    category            INT NOT NULL DEFAULT 1,
    is_success          BOOLEAN NOT NULL DEFAULT FALSE,
    trigger_retry       BOOLEAN NOT NULL DEFAULT FALSE,
    retry_delay_minutes INT,
    trigger_callback    BOOLEAN NOT NULL DEFAULT FALSE,
    is_active           BOOLEAN NOT NULL DEFAULT TRUE,
    sort_order          INT NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS ix_disposition_codes_campaign
    ON disposition_codes (tenant_id, campaign_id);

CREATE TABLE IF NOT EXISTS campaign_schedule_days (
    id          BIGSERIAL PRIMARY KEY,
    campaign_id BIGINT NOT NULL REFERENCES campaigns (id),
    day_of_week INT NOT NULL,
    enabled     BOOLEAN NOT NULL DEFAULT TRUE,
    start_time  TIME NOT NULL DEFAULT '09:00',
    end_time    TIME NOT NULL DEFAULT '18:00'
);

CREATE INDEX IF NOT EXISTS ix_schedule_days_campaign ON campaign_schedule_days (campaign_id);

CREATE TABLE IF NOT EXISTS retry_rules (
    id            BIGSERIAL PRIMARY KEY,
    campaign_id   BIGINT NOT NULL REFERENCES campaigns (id),
    for_status    INT NOT NULL,
    action        INT NOT NULL DEFAULT 1,
    delay_minutes INT,
    max_retries   INT NOT NULL DEFAULT 3
);

CREATE INDEX IF NOT EXISTS ix_retry_rules_campaign ON retry_rules (campaign_id);


-- =============================================================================
-- Pro.Dialer — V4: contacts.metadata JSONB
-- =============================================================================

ALTER TABLE dialer_contacts ADD COLUMN IF NOT EXISTS metadata JSONB DEFAULT '{}';


-- =============================================================================
-- Pro.Dialer — V5: outbound_routes tenant_id + overflow/prefix columns
-- =============================================================================

ALTER TABLE outbound_routes ADD COLUMN IF NOT EXISTS tenant_id        TEXT NOT NULL DEFAULT '';
ALTER TABLE outbound_routes ADD COLUMN IF NOT EXISTS overflow_trunk_id BIGINT;
ALTER TABLE outbound_routes ADD COLUMN IF NOT EXISTS dial_prefix       TEXT;

CREATE INDEX IF NOT EXISTS ix_outbound_routes_tenant ON outbound_routes (tenant_id);
CREATE INDEX IF NOT EXISTS ix_trunks_tenant          ON trunks          (tenant_id);


-- =============================================================================
-- Pro.Dialer — V6: trunks PJSIP fields
-- =============================================================================

ALTER TABLE trunks ADD COLUMN IF NOT EXISTS transport        VARCHAR(40);
ALTER TABLE trunks ADD COLUMN IF NOT EXISTS codecs           VARCHAR(200);
ALTER TABLE trunks ADD COLUMN IF NOT EXISTS auth_username    VARCHAR(40);
ALTER TABLE trunks ADD COLUMN IF NOT EXISTS auth_password    VARCHAR(80);
ALTER TABLE trunks ADD COLUMN IF NOT EXISTS registration_uri VARCHAR(255);
ALTER TABLE trunks ADD COLUMN IF NOT EXISTS client_uri       VARCHAR(255);
ALTER TABLE trunks ADD COLUMN IF NOT EXISTS context          VARCHAR(40);


-- =============================================================================
-- Pro.EventStore — EventStoreSchema (partitioned)
-- =============================================================================

CREATE TABLE IF NOT EXISTS session_events (
    id              BIGINT GENERATED ALWAYS AS IDENTITY,
    session_id      TEXT NOT NULL,
    sequence_number SMALLINT NOT NULL,
    event_type      TEXT NOT NULL,
    server_id       TEXT NOT NULL,
    timestamp       TIMESTAMPTZ NOT NULL,
    payload         JSONB NOT NULL,
    CONSTRAINT pk_session_events PRIMARY KEY (timestamp, id)
) PARTITION BY RANGE (timestamp);

CREATE INDEX IF NOT EXISTS ix_session_events_session
    ON session_events (session_id, sequence_number);

CREATE TABLE IF NOT EXISTS completed_sessions (
    session_id     TEXT PRIMARY KEY,
    server_id      TEXT NOT NULL,
    direction      SMALLINT NOT NULL,
    caller_id_num  TEXT,
    caller_id_name TEXT,
    agent_id       TEXT,
    queue_name     TEXT,
    context        TEXT,
    extension      TEXT,
    started_at     TIMESTAMPTZ NOT NULL,
    connected_at   TIMESTAMPTZ,
    completed_at   TIMESTAMPTZ NOT NULL,
    duration_ms    BIGINT NOT NULL,
    talk_time_ms   BIGINT,
    wait_time_ms   BIGINT,
    hold_time_ms   BIGINT NOT NULL DEFAULT 0,
    hangup_cause   SMALLINT,
    final_state    SMALLINT NOT NULL,
    event_count    SMALLINT NOT NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_completed_sessions_date
    ON completed_sessions (completed_at);
CREATE INDEX IF NOT EXISTS ix_completed_sessions_agent
    ON completed_sessions (agent_id, completed_at) WHERE agent_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_completed_sessions_queue
    ON completed_sessions (queue_name, completed_at) WHERE queue_name IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_completed_sessions_server
    ON completed_sessions (server_id, completed_at);

-- Default partition (catch-all for session_events)
CREATE TABLE IF NOT EXISTS session_events_default
    PARTITION OF session_events DEFAULT;


-- =============================================================================
-- Pro.EventStore — V5: CDR enrichment columns
-- =============================================================================

ALTER TABLE completed_sessions
    ADD COLUMN IF NOT EXISTS recording_name       VARCHAR(256),
    ADD COLUMN IF NOT EXISTS transferred_to       VARCHAR(256),
    ADD COLUMN IF NOT EXISTS transfer_type        SMALLINT,
    ADD COLUMN IF NOT EXISTS transfer_count       SMALLINT DEFAULT 0,
    ADD COLUMN IF NOT EXISTS hangup_source        SMALLINT,
    ADD COLUMN IF NOT EXISTS wrap_up_duration_ms  BIGINT,
    ADD COLUMN IF NOT EXISTS linked_session_id    VARCHAR(128),
    ADD COLUMN IF NOT EXISTS hold_count           SMALLINT DEFAULT 0;


-- =============================================================================
-- Pro.EventStore — V7: completed_sessions.metadata JSONB
-- =============================================================================

ALTER TABLE completed_sessions ADD COLUMN IF NOT EXISTS metadata JSONB;


-- =============================================================================
-- Pro.Analytics — AnalyticsSchema
-- =============================================================================

CREATE TABLE IF NOT EXISTS interval_snapshots (
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    queue_name       TEXT NOT NULL,
    server_id        TEXT NOT NULL,
    interval_start   TIMESTAMPTZ NOT NULL,
    interval_seconds SMALLINT NOT NULL DEFAULT 1800,
    calls_offered    INT NOT NULL DEFAULT 0,
    calls_answered   INT NOT NULL DEFAULT 0,
    calls_abandoned  INT NOT NULL DEFAULT 0,
    short_abandons   INT NOT NULL DEFAULT 0,
    calls_overflowed INT NOT NULL DEFAULT 0,
    sla_met_count    INT NOT NULL DEFAULT 0,
    total_wait_ms    BIGINT NOT NULL DEFAULT 0,
    total_talk_ms    BIGINT NOT NULL DEFAULT 0,
    total_hold_ms    BIGINT NOT NULL DEFAULT 0,
    total_acw_ms     BIGINT NOT NULL DEFAULT 0,
    max_wait_ms      BIGINT NOT NULL DEFAULT 0,
    rna_count        INT NOT NULL DEFAULT 0,
    wait_0_10        INT NOT NULL DEFAULT 0,
    wait_10_20       INT NOT NULL DEFAULT 0,
    wait_20_30       INT NOT NULL DEFAULT 0,
    wait_30_60       INT NOT NULL DEFAULT 0,
    wait_60_120      INT NOT NULL DEFAULT 0,
    wait_120_plus    INT NOT NULL DEFAULT 0,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_interval_queue UNIQUE (queue_name, server_id, interval_start)
);

CREATE INDEX IF NOT EXISTS ix_intervals_time  ON interval_snapshots (interval_start DESC);
CREATE INDEX IF NOT EXISTS ix_intervals_queue ON interval_snapshots (queue_name, interval_start DESC);

CREATE TABLE IF NOT EXISTS agent_snapshots (
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    agent_id         TEXT NOT NULL,
    server_id        TEXT NOT NULL,
    interval_start   TIMESTAMPTZ NOT NULL,
    interval_seconds SMALLINT NOT NULL DEFAULT 1800,
    calls_handled    INT NOT NULL DEFAULT 0,
    total_talk_ms    BIGINT NOT NULL DEFAULT 0,
    total_hold_ms    BIGINT NOT NULL DEFAULT 0,
    total_acw_ms     BIGINT NOT NULL DEFAULT 0,
    rna_count        INT NOT NULL DEFAULT 0,
    transfers        INT NOT NULL DEFAULT 0,
    total_pause_ms   BIGINT NOT NULL DEFAULT 0,
    login_duration_ms BIGINT NOT NULL DEFAULT 0,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_agent_interval UNIQUE (agent_id, server_id, interval_start)
);

CREATE INDEX IF NOT EXISTS ix_agents_time  ON agent_snapshots (interval_start DESC);
CREATE INDEX IF NOT EXISTS ix_agents_agent ON agent_snapshots (agent_id, interval_start DESC);

CREATE TABLE IF NOT EXISTS campaign_snapshots (
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    campaign_id      BIGINT NOT NULL,
    server_id        TEXT NOT NULL,
    interval_start   TIMESTAMPTZ NOT NULL,
    interval_seconds SMALLINT NOT NULL DEFAULT 1800,
    calls_attempted  INT NOT NULL DEFAULT 0,
    calls_connected  INT NOT NULL DEFAULT 0,
    calls_abandoned  INT NOT NULL DEFAULT 0,
    contacts_reached INT NOT NULL DEFAULT 0,
    total_talk_ms    BIGINT NOT NULL DEFAULT 0,
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_campaign_interval UNIQUE (campaign_id, server_id, interval_start)
);


-- =============================================================================
-- Pro.AgentAssist — 001_AgentAssist_Schema
-- =============================================================================

CREATE TABLE IF NOT EXISTS agent_assist_sessions (
    session_id        TEXT        NOT NULL,
    tenant_id         TEXT        NOT NULL DEFAULT '',
    queue_name        TEXT,
    agent_id          TEXT,
    started_at        TIMESTAMPTZ NOT NULL,
    ended_at          TIMESTAMPTZ,
    suggestion_count  INT         NOT NULL DEFAULT 0,
    compliance_alerts INT         NOT NULL DEFAULT 0,
    final_sentiment   REAL,
    transcript_json   JSONB,
    PRIMARY KEY (session_id)
);

CREATE TABLE IF NOT EXISTS suggestion_log (
    id              BIGSERIAL   PRIMARY KEY,
    session_id      TEXT        NOT NULL,
    tenant_id       TEXT        NOT NULL DEFAULT '',
    emitted_at      TIMESTAMPTZ NOT NULL,
    priority        TEXT        NOT NULL,
    source          TEXT        NOT NULL,
    trigger_phrase  TEXT,
    suggestion_text TEXT        NOT NULL,
    whispered       BOOL        NOT NULL DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS compliance_alerts (
    id          BIGSERIAL   PRIMARY KEY,
    session_id  TEXT        NOT NULL,
    tenant_id   TEXT        NOT NULL DEFAULT '',
    occurred_at TIMESTAMPTZ NOT NULL,
    rule_id     TEXT        NOT NULL,
    phrase      TEXT,
    severity    TEXT        NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_suggestion_log_session    ON suggestion_log    (session_id);
CREATE INDEX IF NOT EXISTS idx_suggestion_log_time       ON suggestion_log    (tenant_id, emitted_at);
CREATE INDEX IF NOT EXISTS idx_compliance_alerts_session ON compliance_alerts (session_id);
CREATE INDEX IF NOT EXISTS idx_compliance_alerts_time    ON compliance_alerts (tenant_id, occurred_at);


-- =============================================================================
-- Pro.CallAnalytics — 001_CallAnalytics_Schema
-- =============================================================================

CREATE TABLE IF NOT EXISTS call_analysis_results (
    session_id              TEXT             NOT NULL,
    tenant_id               TEXT             NOT NULL,
    queue_name              TEXT,
    agent_id                TEXT,
    analyzed_at             TIMESTAMPTZ      NOT NULL,
    processing_ms           INTEGER          NOT NULL,
    summary_reason          TEXT,
    summary_outcome         TEXT,
    summary_narrative       TEXT,
    summary_action_items    JSONB,
    summary_disposition     TEXT,
    qa_total_score          DOUBLE PRECISION,
    qa_max_score            DOUBLE PRECISION,
    qa_items                JSONB,
    sentiment_overall       REAL,
    sentiment_label         TEXT,
    sentiment_start         TEXT,
    sentiment_end           TEXT,
    sentiment_trend         TEXT,
    sentiment_turns         JSONB,
    primary_topic           TEXT,
    all_topics              JSONB,
    compliance_count        INTEGER          NOT NULL DEFAULT 0,
    has_critical            BOOLEAN          NOT NULL DEFAULT FALSE,
    agent_talk_ratio        DOUBLE PRECISION,
    total_silence_ms        INTEGER,
    silence_count           INTEGER,
    interruption_count      INTEGER,
    agent_turn_count        INTEGER,
    caller_turn_count       INTEGER,
    total_agent_talk_ms     INTEGER,
    total_caller_talk_ms    INTEGER,
    longest_agent_mono_ms   INTEGER,
    longest_caller_mono_ms  INTEGER,
    PRIMARY KEY (tenant_id, session_id)
);

CREATE INDEX IF NOT EXISTS ix_call_analysis_tenant_date
    ON call_analysis_results (tenant_id, analyzed_at DESC);
CREATE INDEX IF NOT EXISTS ix_call_analysis_queue
    ON call_analysis_results (queue_name) WHERE queue_name IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_call_analysis_agent
    ON call_analysis_results (agent_id) WHERE agent_id IS NOT NULL;

CREATE TABLE IF NOT EXISTS call_compliance_violations (
    id               BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id       TEXT NOT NULL,
    tenant_id        TEXT NOT NULL,
    rule_id          TEXT NOT NULL,
    rule_name        TEXT NOT NULL,
    severity         TEXT NOT NULL,
    description      TEXT NOT NULL,
    evidence         TEXT,
    transcript_index INTEGER,
    FOREIGN KEY (tenant_id, session_id)
        REFERENCES call_analysis_results (tenant_id, session_id)
);

CREATE INDEX IF NOT EXISTS ix_compliance_session
    ON call_compliance_violations (tenant_id, session_id);

CREATE TABLE IF NOT EXISTS call_qa_reviews (
    id             BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id     TEXT             NOT NULL,
    tenant_id      TEXT             NOT NULL,
    reviewer_id    TEXT             NOT NULL,
    reviewed_at    TIMESTAMPTZ      NOT NULL,
    score_override DOUBLE PRECISION,
    notes          TEXT,
    FOREIGN KEY (tenant_id, session_id)
        REFERENCES call_analysis_results (tenant_id, session_id)
);
