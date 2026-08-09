CREATE TABLE auth_totp_state (
    user_id bigint PRIMARY KEY REFERENCES user_accounts(id) ON DELETE CASCADE,
    failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    window_started_at timestamptz NOT NULL DEFAULT now(),
    locked_until timestamptz,
    last_accepted_step bigint,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_auth_totp_state_locked_until
    ON auth_totp_state(locked_until)
    WHERE locked_until IS NOT NULL;
