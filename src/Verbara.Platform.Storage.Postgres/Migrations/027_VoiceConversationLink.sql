-- 027_VoiceConversationLink.sql
--
-- Phase 3B.0 (voice as a tracked Conversation) — per-call idempotency correlation.
--
-- A voice Conversation is created by VoiceConversationBridge from the live AMI
-- session pipeline. Because the AMI event stream is a broadcast (every connected
-- pod's CallSessionManager observes the same events) and leadership can fail over
-- mid-call, the create path must be exactly-once across the cluster. The
-- leader-emit gate (voice:ami:owner:leader) is the primary mechanism; this column
-- + partial unique index is the failover safety net: the call's Asterisk LinkedId
-- (the call-global id shared by every channel of one call) becomes a unique
-- correlation key, so a re-emission for the same in-flight call (old leader +
-- new leader during a flap) can never insert a duplicate Conversation — the
-- second insert hits the unique index and the store treats it as already-tracked.
--
-- Semantics:
--   * voice_linked_id is NULL for every non-voice conversation (and for any voice
--     conversation that predates this column) — the index is PARTIAL so those
--     rows are unconstrained, mirroring 026_DidRoutes' partial-index style.
--   * Uniqueness is scoped (tenant_id, voice_linked_id): the same LinkedId in two
--     tenants never collides, and within a tenant one call ⇒ one Conversation.
--   * Idempotent (ADD COLUMN IF NOT EXISTS / CREATE UNIQUE INDEX IF NOT EXISTS) so
--     a re-attempted migration is a no-op.

ALTER TABLE conversations ADD COLUMN IF NOT EXISTS voice_linked_id TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS uq_conversations_voice_linked_id
    ON conversations (tenant_id, voice_linked_id)
    WHERE voice_linked_id IS NOT NULL;
