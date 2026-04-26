-- 020_ApiKeysLastUsedAt.sql
--
-- R5.2 PC.5 / B.12 — track last-used timestamp on API keys.
--
-- Closes the R5.1 limitation #12 backend gap: the Web admin API keys page
-- was shipped with a `—` placeholder for "Last used" because the schema had
-- no column to source it from. The auth middleware now stamps
-- `last_used_at = NOW()` whenever a key validates successfully, debounced
-- in-process to ≤ 1 write per minute per key (avoids write amplification on
-- chatty clients while still providing near-real-time visibility for
-- operators auditing key usage).
--
-- The column is nullable: existing rows surface as `NULL` until the next
-- successful auth attempt against that key — the Web column maps `NULL`
-- to a "Never" placeholder.
--
-- Rollback: ALTER TABLE api_keys DROP COLUMN last_used_at;

ALTER TABLE api_keys ADD COLUMN IF NOT EXISTS last_used_at TIMESTAMPTZ NULL;
