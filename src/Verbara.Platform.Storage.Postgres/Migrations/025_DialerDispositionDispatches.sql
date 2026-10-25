-- =============================================================================
-- Verbara.Platform — the dispositions typifications owe the dialer (025)
-- =============================================================================
-- dialer-honours-dnc-callbacks-and-agent-dispositions (design D16; the claim
-- shape of Platform/ADR-0043, as 023_WebhookDeliveryClaim).
-- A typification of a campaign call stores its submission and, in the same
-- transaction, one row here carrying everything the dialer needs to apply the
-- disposition (campaign, action id, dialer contact, code, comment, callback
-- time, the leaf's callback request, agent). The row is handed to the dialer
-- by the request, then by any instance's dispatcher, until the dialer answers:
--   * claim_id / claim_expires_at — the fencing id of the hand-off holding the
--                        row and its lease; only that holder records the answer,
--                        and once the lease passed any instance may claim it;
--   * attempts         — how many claims the row has had (the request's is the
--                        first);
--   * next_attempt_at  — when a dispatcher may claim it next, after a failure;
--   * result / completed_at — the dialer's answer (an AgentDispositionResult
--                        name) and when it was recorded; NULL while pending;
--   * published_at     — when the campaign.disposition_submitted event was
--                        published for it, NULL when none was;
--   * last_error       — the last hand-off failure, for the operator.
-- Every lease time is written from the Api's clock, never NOW().
-- Completed rows are kept: one per typified campaign call, beside its
-- submission. The partial index serves the dispatcher's due-row claim and the
-- pending count.
--
-- Additive, no backfill, idempotent (IF NOT EXISTS). Rollback: an older Api
-- neither reads nor writes the table; to remove it,
--   DROP TABLE IF EXISTS dialer_disposition_dispatches;
-- after the rows without completed_at were handed over (a dropped pending row
-- is a disposition the dialer never receives).
-- =============================================================================

CREATE TABLE IF NOT EXISTS dialer_disposition_dispatches (
    tenant_id            TEXT         NOT NULL,
    conversation_id      TEXT         NOT NULL,
    campaign_id          BIGINT       NOT NULL,
    action_id            TEXT         NOT NULL,
    dialer_contact_id    BIGINT       NULL,
    disposition_code_id  BIGINT       NOT NULL,
    disposition_code     TEXT         NOT NULL,
    comment              TEXT         NULL,
    callback_at          TIMESTAMPTZ  NULL,
    requests_callback    BOOLEAN      NOT NULL DEFAULT FALSE,
    agent_id             TEXT         NULL,
    attempts             INTEGER      NOT NULL DEFAULT 0,
    next_attempt_at      TIMESTAMPTZ  NOT NULL,
    claim_id             VARCHAR(32)  NULL,
    claim_expires_at     TIMESTAMPTZ  NULL,
    result               TEXT         NULL,
    completed_at         TIMESTAMPTZ  NULL,
    published_at         TIMESTAMPTZ  NULL,
    last_error           TEXT         NULL,
    created_at           TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, conversation_id)
);

CREATE INDEX IF NOT EXISTS ix_dialer_disposition_dispatches_pending
    ON dialer_disposition_dispatches (next_attempt_at)
    WHERE completed_at IS NULL;
