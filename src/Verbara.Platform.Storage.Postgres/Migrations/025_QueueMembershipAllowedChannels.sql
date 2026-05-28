-- ADR-0026 Phase A.0 — Channel-aware queue membership.
--
-- Adds `allowed_channels TEXT[]` to `queue_memberships`. Semantics:
--   NULL  (default for existing rows) → member for ALL channels the queue
--          accepts (preserves implicit pre-v2.6.0 behavior — backward compatible).
--   ['voice']               → only voice for this agent in this queue.
--   ['webchat','email']     → only these digital channels; voice excluded.
--   '{}' (empty array)      → equivalent to is_excluded=true. Validation at
--          the API layer rejects empty arrays and suggests is_excluded.
--
-- The actual routing executive gate (MembershipGateMiddleware) ships in
-- Phase B (v2.6.0). In Phase A (v2.5.5) this column is read by:
--   • CreateAgent endpoint — to drive conditional Asterisk sync via
--     IRealtimeSyncService.AddQueueMemberAsync (skipped when voice not in
--     allowed_channels).
--   • Admin agentes UI editor — to show/edit channel restrictions per
--     membership.
--
-- Index rationale: routing filters by (tenant_id, queue_id) → allowed_channels
-- is included in the result set, not used as a search predicate. No index
-- needed on this column.

ALTER TABLE queue_memberships
    ADD COLUMN IF NOT EXISTS allowed_channels TEXT[];
