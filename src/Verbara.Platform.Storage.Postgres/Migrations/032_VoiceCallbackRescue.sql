-- 032_VoiceCallbackRescue.sql — W5b (voice) caller-rescue via priority callback.
-- Per-tenant grace (seconds) a dropped voice caller waits before the rescue callback is
-- originated, measured from the call's WrapUp. Default 25. 0 disables voice rescue.
ALTER TABLE tenant_auth_config
  ADD COLUMN IF NOT EXISTS voice_callback_grace_seconds integer NOT NULL DEFAULT 25;
