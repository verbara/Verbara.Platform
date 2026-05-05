-- Migration 008: Add key_type column to api_keys
-- Supports ApiKeyType enum: Standard = 0, Management = 1

ALTER TABLE api_keys ADD COLUMN IF NOT EXISTS key_type INTEGER NOT NULL DEFAULT 0;
