ALTER TABLE user_subscriptions
    ADD COLUMN IF NOT EXISTS quota_reserved_usd numeric(20,8) NOT NULL DEFAULT 0;

ALTER TABLE user_subscriptions
    DROP CONSTRAINT IF EXISTS ck_user_subscriptions_quota_reservation;
ALTER TABLE user_subscriptions
    ADD CONSTRAINT ck_user_subscriptions_quota_reservation
    CHECK (quota_granted_usd >= 0 AND quota_used_usd >= 0 AND quota_reserved_usd >= 0);

ALTER TABLE request_leases
    ADD COLUMN IF NOT EXISTS subscription_id bigint
        REFERENCES user_subscriptions(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS subscription_hold_amount numeric(20,8) NOT NULL DEFAULT 0;

ALTER TABLE request_leases
    DROP CONSTRAINT IF EXISTS ck_request_leases_subscription_hold;
ALTER TABLE request_leases
    ADD CONSTRAINT ck_request_leases_subscription_hold
    CHECK (subscription_hold_amount >= 0);

CREATE INDEX IF NOT EXISTS ix_user_subscriptions_quota_reservation
    ON user_subscriptions(user_id, status, expires_at, id);
CREATE INDEX IF NOT EXISTS ix_request_leases_subscription
    ON request_leases(subscription_id)
    WHERE subscription_id IS NOT NULL;
