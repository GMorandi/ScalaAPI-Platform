-- Rotating user/admin sessions. Access JWTs carry session_id and are rejected
-- after this row is revoked; refresh tokens are stored only as SHA-256 hashes.
CREATE TABLE auth_sessions (
    session_id text PRIMARY KEY,
    user_id bigint NOT NULL REFERENCES user_accounts(id) ON DELETE CASCADE,
    email text NOT NULL,
    role text NOT NULL,
    refresh_token_hash text NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    replaced_by text,
    ip_address text,
    user_agent text
);

CREATE INDEX ix_auth_sessions_user_active
    ON auth_sessions(user_id, created_at DESC)
    WHERE revoked_at IS NULL;
CREATE INDEX ix_auth_sessions_expiry
    ON auth_sessions(expires_at)
    WHERE revoked_at IS NULL;
