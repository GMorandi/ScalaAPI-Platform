CREATE TABLE IF NOT EXISTS provider_quota_state (
    account_id bigint NOT NULL,
    tier text NOT NULL DEFAULT 'unknown',
    remaining_quota numeric(20,8),
    window_start timestamptz,
    window_end timestamptz,
    source text,
    fetched_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz,
    generation bigint NOT NULL DEFAULT 0,
    refresh_lock_until timestamptz,
    refresh_lock_token text,
    cooldown_until timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id)
);
CREATE INDEX IF NOT EXISTS idx_provider_quota_expires ON provider_quota_state(expires_at);

CREATE TABLE IF NOT EXISTS provider_quota_reservations (
    lease_id text PRIMARY KEY,
    account_id bigint NOT NULL,
    estimated_cost numeric(20,8) NOT NULL,
    actual_cost numeric(20,8),
    status text NOT NULL DEFAULT 'reserved',
    created_at timestamptz NOT NULL DEFAULT now(),
    settled_at timestamptz
);
CREATE INDEX IF NOT EXISTS idx_pqr_account ON provider_quota_reservations(account_id);
