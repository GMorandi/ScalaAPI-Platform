ALTER TABLE user_accounts
    ADD COLUMN IF NOT EXISTS email_verified boolean NOT NULL DEFAULT false;
ALTER TABLE user_accounts
    ADD COLUMN IF NOT EXISTS email_verified_at timestamptz;

CREATE TABLE IF NOT EXISTS email_verification_tokens (
    token_hash text PRIMARY KEY,
    user_id bigint NOT NULL REFERENCES user_accounts(id) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    used_at timestamptz
);

CREATE INDEX IF NOT EXISTS ix_email_verification_tokens_user_active
    ON email_verification_tokens(user_id, expires_at)
    WHERE used_at IS NULL;
