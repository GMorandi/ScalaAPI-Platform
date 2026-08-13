-- Subscription purchases: individual purchase events linked to payment orders.
-- Drives entitlement: a subscription is only active after payment confirmation.
CREATE TABLE IF NOT EXISTS subscription_purchases (
    purchase_id bigserial PRIMARY KEY,
    user_id bigint NOT NULL,
    plan_id text NOT NULL,
    payment_order_id bigint,
    started_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    status text NOT NULL DEFAULT 'active',
    auto_renew boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(user_id, plan_id, started_at)
);
CREATE INDEX IF NOT EXISTS ix_subscription_purchases_user
    ON subscription_purchases(user_id, status);
CREATE INDEX IF NOT EXISTS ix_subscription_purchases_payment
    ON subscription_purchases(payment_order_id)
    WHERE payment_order_id IS NOT NULL;

-- Redemption codes: plan-based codes with concurrency/expiry/usage-limit controls.
CREATE TABLE IF NOT EXISTS redemption_codes (
    code_id text PRIMARY KEY,
    code_hash text NOT NULL UNIQUE,
    plan_id text NOT NULL,
    max_uses integer NOT NULL DEFAULT 1,
    current_uses integer NOT NULL DEFAULT 0,
    expires_at timestamptz,
    promotion_id text,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_redemption_codes_expiry
    ON redemption_codes(expires_at)
    WHERE expires_at IS NOT NULL;

-- Redemption history: tracks which user redeemed which code.
-- UNIQUE(code_id, user_id) prevents duplicate redemptions (one entitlement per user per code).
CREATE TABLE IF NOT EXISTS redemption_history (
    redemption_id bigserial PRIMARY KEY,
    code_id text NOT NULL,
    user_id bigint NOT NULL,
    redeemed_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(code_id, user_id)
);
CREATE INDEX IF NOT EXISTS ix_redemption_history_user
    ON redemption_history(user_id, redeemed_at DESC);

-- Referral attributions: signup-time referral tracking with anti-abuse.
-- UNIQUE(referred_user_id) ensures one referral per user (anti-abuse).
CREATE TABLE IF NOT EXISTS referral_attributions (
    referral_id bigserial PRIMARY KEY,
    referrer_user_id bigint NOT NULL,
    referred_user_id bigint NOT NULL UNIQUE,
    rebate_amount numeric(20,8) NOT NULL DEFAULT 0,
    status text NOT NULL DEFAULT 'pending',
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_referral_attributions_referrer
    ON referral_attributions(referrer_user_id, created_at DESC);

-- Extend announcements with targeting, scheduling, and authorship.
ALTER TABLE announcements
    ADD COLUMN IF NOT EXISTS target_audience text NOT NULL DEFAULT 'all',
    ADD COLUMN IF NOT EXISTS scheduled_at timestamptz,
    ADD COLUMN IF NOT EXISTS published_at timestamptz,
    ADD COLUMN IF NOT EXISTS created_by bigint;

CREATE INDEX IF NOT EXISTS ix_announcements_schedule
    ON announcements(scheduled_at, status)
    WHERE scheduled_at IS NOT NULL AND status = 'draft';
