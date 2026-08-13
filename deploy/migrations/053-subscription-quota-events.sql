-- Migration 053: Subscription Quota Idempotency Events
-- This table tracks subscription quota operations to prevent double-counting
-- when reserve/commit/release methods are called multiple times due to retries.

CREATE TABLE IF NOT EXISTS subscription_quota_events (
    lease_token TEXT NOT NULL PRIMARY KEY,
    subscription_id BIGINT NOT NULL,
    event_type TEXT NOT NULL CHECK (event_type IN ('reserved', 'committed', 'released')),
    reserved_amount NUMERIC(20,8) NOT NULL,
    used_amount NUMERIC(20,8) NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Index for efficient lookup by subscription
CREATE INDEX IF NOT EXISTS idx_subscription_quota_events_subscription_id
ON subscription_quota_events(subscription_id);

-- Index for efficient lookup by event type
CREATE INDEX IF NOT EXISTS idx_subscription_quota_events_event_type
ON subscription_quota_events(event_type);
