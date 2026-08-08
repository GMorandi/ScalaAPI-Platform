-- Password recovery is a product-owned, single-use capability. Tokens are
-- persisted only as SHA-256 hashes and are invalidated after consumption.
CREATE TABLE IF NOT EXISTS password_reset_tokens (
    token_hash text PRIMARY KEY,
    user_id bigint NOT NULL REFERENCES user_accounts(id) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    used_at timestamptz
);

CREATE INDEX IF NOT EXISTS ix_password_reset_tokens_user_active
    ON password_reset_tokens(user_id, expires_at)
    WHERE used_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_password_reset_tokens_expiry
    ON password_reset_tokens(expires_at)
    WHERE used_at IS NULL;
