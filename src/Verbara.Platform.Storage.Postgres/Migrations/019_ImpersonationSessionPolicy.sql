-- 019_ImpersonationSessionPolicy.sql
--
-- R5.2 PB.2 / C.7 — adds the per-tenant impersonation session policy fields
-- to `tenant_auth_config`:
--   * impersonation_max_concurrent_sessions (default 3) — caps the number of
--     simultaneously open impersonation sessions per actor tenant. 0 disables
--     the cap (back-compat for installations that managed concurrency
--     out-of-band).
--   * impersonation_auto_timeout_minutes (default 240 = 4h) — periodic sweep
--     forcibly revokes sessions older than this. 0 disables the sweep for
--     the tenant.
--
-- Defaults match the new field defaults on `TenantAuthConfig` so existing
-- rows automatically pick up the platform-wide policy without an update
-- statement; the explicit DEFAULT clause is kept so future inserts that omit
-- the columns continue to land on the same values.

ALTER TABLE tenant_auth_config
    ADD COLUMN IF NOT EXISTS impersonation_max_concurrent_sessions INTEGER NOT NULL DEFAULT 3,
    ADD COLUMN IF NOT EXISTS impersonation_auto_timeout_minutes    INTEGER NOT NULL DEFAULT 240;
