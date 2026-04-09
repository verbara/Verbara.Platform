-- 014_CannedResponses.sql
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

-- Bot analytics
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
