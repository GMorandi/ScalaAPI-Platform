-- 051: Persistent concurrency slot authority (cross-Silo)
-- Makes account/user concurrency slots durable in PostgreSQL so that
-- multiple silos share a single source of truth for slot accounting.

-- Account concurrency slots
CREATE TABLE account_concurrency_slots (
    account_id BIGINT NOT NULL PRIMARY KEY,
    generation BIGINT NOT NULL DEFAULT 1,
    active_count INT NOT NULL DEFAULT 0,
    max_concurrency INT NOT NULL DEFAULT 1,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE account_slot_leases (
    lease_token TEXT PRIMARY KEY,
    account_id BIGINT NOT NULL REFERENCES account_concurrency_slots(account_id),
    request_id TEXT NOT NULL,
    owner_silo_id TEXT NOT NULL,
    generation BIGINT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    acquired_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    status TEXT NOT NULL DEFAULT 'active',
    released_at TIMESTAMPTZ
);

CREATE INDEX ix_account_slot_leases_account_status
    ON account_slot_leases(account_id, status) WHERE status = 'active';

CREATE INDEX ix_account_slot_leases_expires
    ON account_slot_leases(account_id, expires_at) WHERE status = 'active';

-- User concurrency slots (same pattern)
CREATE TABLE user_concurrency_slots (
    user_id BIGINT NOT NULL PRIMARY KEY,
    generation BIGINT NOT NULL DEFAULT 1,
    active_count INT NOT NULL DEFAULT 0,
    max_concurrency INT NOT NULL DEFAULT 1,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE user_slot_leases (
    lease_token TEXT PRIMARY KEY,
    user_id BIGINT NOT NULL REFERENCES user_concurrency_slots(user_id),
    request_id TEXT NOT NULL,
    owner_silo_id TEXT NOT NULL,
    generation BIGINT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    acquired_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    status TEXT NOT NULL DEFAULT 'active',
    released_at TIMESTAMPTZ
);

CREATE INDEX ix_user_slot_leases_user_status
    ON user_slot_leases(user_id, status) WHERE status = 'active';

CREATE INDEX ix_user_slot_leases_expires
    ON user_slot_leases(user_id, expires_at) WHERE status = 'active';

-- Account health state
CREATE TABLE account_health_state (
    account_id BIGINT PRIMARY KEY,
    consecutive_errors INT NOT NULL DEFAULT 0,
    last_success_at TIMESTAMPTZ,
    rate_limit_reset_at TIMESTAMPTZ,
    overload_until TIMESTAMPTZ,
    temp_unschedulable_until TIMESTAMPTZ,
    disabled_permanently BOOLEAN NOT NULL DEFAULT false,
    disable_reason TEXT,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
