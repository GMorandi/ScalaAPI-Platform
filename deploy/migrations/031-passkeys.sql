-- Native WebAuthn passkey ceremonies and credential state.
-- Challenges are one-shot server options; credential public keys/counters are
-- the only durable authenticator material. No Sub2API identity data is reused.
CREATE TABLE IF NOT EXISTS passkey_challenges (
    challenge_id uuid PRIMARY KEY,
    user_id bigint NOT NULL,
    flow text NOT NULL,
    options jsonb NOT NULL,
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_passkey_challenge_flow CHECK (flow IN ('registration', 'authentication'))
);
CREATE INDEX IF NOT EXISTS ix_passkey_challenges_user_flow
    ON passkey_challenges(user_id, flow, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_passkey_challenges_expiry
    ON passkey_challenges(expires_at) WHERE consumed_at IS NULL;

CREATE TABLE IF NOT EXISTS passkey_credentials (
    credential_id bytea PRIMARY KEY,
    user_id bigint NOT NULL,
    user_handle bytea NOT NULL,
    public_key bytea NOT NULL,
    signature_counter bigint NOT NULL DEFAULT 0,
    display_name text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz,
    CONSTRAINT ck_passkey_credential_counter CHECK (signature_counter >= 0)
);
CREATE INDEX IF NOT EXISTS ix_passkey_credentials_user
    ON passkey_credentials(user_id, created_at DESC);
