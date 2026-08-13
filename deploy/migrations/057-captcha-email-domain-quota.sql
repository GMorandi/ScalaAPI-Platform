CREATE TABLE IF NOT EXISTS captcha_challenges (
    id              bigserial PRIMARY KEY,
    challenge_nonce text NOT NULL,
    provider        text NOT NULL DEFAULT 'mock',
    action          text NOT NULL DEFAULT 'register',
    token_hash      text NOT NULL DEFAULT '',
    score           real,
    consumed_at     timestamptz,
    expires_at      timestamptz NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_captcha_challenges_nonce ON captcha_challenges (challenge_nonce);
CREATE INDEX IF NOT EXISTS idx_captcha_challenges_expires ON captcha_challenges(expires_at) WHERE consumed_at IS NULL;

CREATE TABLE IF NOT EXISTS email_domain_registration_quota (
    domain      text NOT NULL,
    quota_date  date NOT NULL DEFAULT CURRENT_DATE,
    count       integer NOT NULL DEFAULT 0,
    updated_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (domain, quota_date)
);

CREATE TABLE IF NOT EXISTS captcha_config (
    config_key      text PRIMARY KEY DEFAULT 'default',
    provider        text NOT NULL DEFAULT 'mock',
    site_key        text NOT NULL DEFAULT '',
    secret_key      text NOT NULL DEFAULT '',
    enabled         boolean NOT NULL DEFAULT false,
    score_threshold real NOT NULL DEFAULT 0.5,
    challenge_ttl   integer NOT NULL DEFAULT 300,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      bigint
);
