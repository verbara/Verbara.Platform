-- =============================================================================
-- Verbara.Platform — the host tenant holds an explicit Enterprise plan (021)
-- =============================================================================
-- partner-channel-pilot-fixes (design D6; partner-channel-tenancy, decision 9).
-- /api/setup used to create the Platform (host) tenant with no metadata, so its
-- plan read as the Starter fallback of TenantExtensions.GetPlan(), and the plan
-- ceiling then capped every Partner and direct Customer at Starter. Setup now
-- writes Plan = Enterprise on the host; this backfills deployments set up before.
--
-- Idempotent and conservative:
--   * only the Platform tenant (type = 0) is touched;
--   * a host that already holds an explicit "Plan" key keeps it, whatever it is;
--   * every other metadata key is kept (the key is merged, not replaced);
--   * a second run matches no row and changes nothing.
-- Tenants other than the host are not backfilled: inheritance at birth applies to
-- tenants created from this version on (design D15), and raising live tenants'
-- plans silently is not something anyone decided.
-- =============================================================================

UPDATE tenants
SET metadata = (CASE WHEN jsonb_typeof(metadata) = 'object' THEN metadata ELSE '{}'::jsonb END)
               || '{"Plan":"Enterprise"}'::jsonb,
    updated_at = now()
WHERE type = 0
  AND (metadata IS NULL OR jsonb_typeof(metadata) <> 'object' OR NOT metadata ? 'Plan');
