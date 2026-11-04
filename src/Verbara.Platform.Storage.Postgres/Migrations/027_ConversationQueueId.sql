-- =============================================================================
-- Verbara.Platform — the last queue of a conversation (027)
-- =============================================================================
-- contact-history-opens-by-assignment (design D7; Platform/ADR-0047).
--
-- conversations.queue_id is the last queue that owned the conversation: the
-- contact history shows it (queue_configs.name) after an agent took the
-- conversation over, which overwrites owner_kind/owner_id. From here on it is
-- written by the store only, in the whole-row upsert, with one CASE kept as the
-- constant PostgresConversationStore.QueueIdWriteCase: a save whose owner is a
-- queue (owner_kind = 3) stores that queue, and every other save keeps the
-- stored value, so it never clears. The claim statements and the
-- compare-and-set closes do not touch it. Off the wire (Conversation.QueueId is
-- [JsonIgnore]).
--
-- Backfill, in order:
--   1. add the column nullable;
--   2. every row a queue owns now gets that queue;
--   3. every other row stays NULL: no source records the queue an agent-owned
--      or closed row came through (no metadata key, and the audit trail is not
--      a reliable source), so those rows cannot be filled backwards.
-- No index: the contact history reaches conversations by
-- idx_conversations_contact.
--
-- Locks (the runner wraps the file in one transaction): ADD COLUMN of a
-- nullable column with no default is catalog-only; the backfill UPDATE takes
-- row locks on the queue-owned rows only.
--
-- Idempotent: ADD COLUMN IF NOT EXISTS, and the UPDATE touches only rows still
-- NULL, so a second run changes nothing (a queue the store wrote meanwhile is
-- never overwritten). Rollback: an older binary never names the column; its
-- upsert leaves it as it is. During a rolling deploy an older replica's save of
-- a queue owner does not record the queue (the upgrade note's queue_id gap). To
-- remove it,
--   ALTER TABLE conversations DROP COLUMN IF EXISTS queue_id;
-- =============================================================================

ALTER TABLE conversations ADD COLUMN IF NOT EXISTS queue_id TEXT NULL;

UPDATE conversations
SET queue_id = owner_id
WHERE queue_id IS NULL
  AND owner_kind = 3
  AND owner_id IS NOT NULL;
