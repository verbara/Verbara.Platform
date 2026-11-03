-- =============================================================================
-- Verbara.Platform — the inactivity clock and the per-tenant window (026)
-- =============================================================================
-- conversations-close-after-inactivity (design D2, D11; Platform/ADR-0046).
--
-- conversations.last_message_at is the clock of the inactivity close: the time
-- of the conversation's last customer-facing activity. From here on it is
-- written only by the claim statements (an inbound message, an agent or bot
-- send), with the platform's clock and monotonically; a new row starts at its
-- creation; the whole-row save never writes it.
--
-- Backfill, in order:
--   1. add the column nullable;
--   2. every non-terminal row (Closed 50, Abandoned 51, Merged 52, Spam 53 are
--      terminal) gets the newest ACTIVITY message, else its created_at. An
--      activity message is an Inbound message, or an Outbound one not stored
--      Failed (a send refused before it reached a connector never counts);
--      System messages (notes, transfer notes) never count. The predicate is
--      the text of PostgresConversationStore.ActivityMessagePredicate, and the
--      upgrade note's dry-run repeats it. A message's created_at is the
--      provider's timestamp for an inbound message (the only time an older
--      binary leaves), so the backfill reads that;
--   3. every remaining (terminal) row gets created_at; no sweep reads it;
--   4. SET DEFAULT now(), after the backfill so it rewrites nothing: it only
--      serves an older binary's INSERT during a rolling deploy or a rollback;
--   5. SET NOT NULL;
--   6. the sweep's index (tenant_id, state, last_message_at), which also serves
--      ListByStateAsync.
-- Then tenant_auth_config.inactivity_close_after_hours: a nullable per-tenant
-- override of the host window (Conversations:InactivityCloseAfterHours), null
-- for every existing row, set by SQL only; no save of the application names it,
-- and an older binary's upsert leaves it as it is. Catalog-only.
--
-- Locks (the runner wraps the file in one transaction):
--   * the backfill UPDATEs take row locks on every conversations row;
--   * SET NOT NULL validates by scanning conversations under an ACCESS
--     EXCLUSIVE lock (reads and writes wait for the scan);
--   * CREATE INDEX (not CONCURRENTLY: it cannot run inside the runner's
--     transaction) blocks writes on conversations while it builds; reads go on.
-- On a large table run the upgrade in a quiet window (upgrade note).
--
-- Idempotent: every step is IF NOT EXISTS or touches only rows still NULL, so a
-- second run changes nothing. Rollback: an older binary never names either
-- column; its inserts get now() and its updates leave both as they are. To
-- remove them,
--   DROP INDEX IF EXISTS idx_conversations_state_last_message;
--   ALTER TABLE conversations DROP COLUMN IF EXISTS last_message_at;
--   ALTER TABLE tenant_auth_config DROP COLUMN IF EXISTS inactivity_close_after_hours;
-- =============================================================================

ALTER TABLE conversations ADD COLUMN IF NOT EXISTS last_message_at TIMESTAMPTZ NULL;

UPDATE conversations c
SET last_message_at = COALESCE(
    (SELECT max(m.created_at) FROM messages m
     WHERE m.tenant_id = c.tenant_id AND m.conversation_id = c.conversation_id
       AND (m.direction = 0 OR (m.direction = 1 AND m.delivery_status <> 3))),
    c.created_at)
WHERE c.last_message_at IS NULL
  AND c.state NOT IN (50, 51, 52, 53);

UPDATE conversations SET last_message_at = created_at WHERE last_message_at IS NULL;

ALTER TABLE conversations ALTER COLUMN last_message_at SET DEFAULT now();
ALTER TABLE conversations ALTER COLUMN last_message_at SET NOT NULL;

CREATE INDEX IF NOT EXISTS idx_conversations_state_last_message
    ON conversations (tenant_id, state, last_message_at);

ALTER TABLE tenant_auth_config ADD COLUMN IF NOT EXISTS inactivity_close_after_hours INTEGER NULL;
