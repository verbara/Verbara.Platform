-- 016_SessionLastActivity.sql
-- Adds last_activity_at to refresh_tokens for tenant-wide session listing
-- (foundation for admin active-session view).

ALTER TABLE refresh_tokens ADD COLUMN IF NOT EXISTS last_activity_at TIMESTAMPTZ;
UPDATE refresh_tokens SET last_activity_at = created_at WHERE last_activity_at IS NULL;
ALTER TABLE refresh_tokens ALTER COLUMN last_activity_at SET NOT NULL;
ALTER TABLE refresh_tokens ALTER COLUMN last_activity_at SET DEFAULT now();
