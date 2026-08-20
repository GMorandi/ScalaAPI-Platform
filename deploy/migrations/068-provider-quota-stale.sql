-- Add stale tracking columns to provider_quota_state.
ALTER TABLE provider_quota_state
    ADD COLUMN IF NOT EXISTS consecutive_failures int NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS state text NOT NULL DEFAULT 'fresh'
        CHECK (state IN ('fresh', 'stale'));
