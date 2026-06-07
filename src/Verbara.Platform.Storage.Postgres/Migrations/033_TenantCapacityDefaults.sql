-- 033_TenantCapacityDefaults.sql — W6 (agent channel-capacity).
-- W6: per-tenant default channel capacity (agents inherit these unless they carry a per-field override).
ALTER TABLE tenant_auth_config
    ADD COLUMN IF NOT EXISTS max_voice_default integer NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS max_chat_default  integer NOT NULL DEFAULT 3,
    ADD COLUMN IF NOT EXISTS max_email_default integer NOT NULL DEFAULT 5,
    ADD COLUMN IF NOT EXISTS max_sms_default   integer NOT NULL DEFAULT 3,
    ADD COLUMN IF NOT EXISTS max_total_default integer NOT NULL DEFAULT 5;

-- W6 legacy-data normalization (REQUIRED — see review finding I-1):
-- Before W6 there was NO way to set per-agent capacity (no DTO field, no endpoint), so EVERY existing
-- agents.capacity value is the old hard-coded default object {maxVoice:1,maxChat:3,maxEmail:5,maxSms:3,maxTotal:5}
-- (serialized on every prior SaveAsync) or '{}'. Under the new per-field-nullable override model a fully
-- populated object would PIN every field and permanently shadow the tenant default, defeating "retune tenant
-- default = one edit". Reset all non-empty rows to '{}' so they cleanly INHERIT the tenant default.
-- agents.capacity is `jsonb NOT NULL DEFAULT '{}'`, so the IS DISTINCT FROM comparison is jsonb-vs-jsonb
-- (structural, key-order-insensitive) and the column can never be NULL.
UPDATE agents SET capacity = '{}'::jsonb WHERE capacity IS DISTINCT FROM '{}'::jsonb;
