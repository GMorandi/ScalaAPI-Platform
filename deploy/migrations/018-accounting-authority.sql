-- PostgreSQL owns the ordered monetary state for each product user. Every
-- ledger effect advances exactly one per-user version and queues the latest
-- authoritative snapshot for Orleans projection.
CREATE TABLE IF NOT EXISTS accounting_accounts (
    user_id bigint PRIMARY KEY,
    posted_balance numeric(20,8) NOT NULL DEFAULT 0,
    ledger_version bigint NOT NULL DEFAULT 0 CHECK (ledger_version >= 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE balance_ledger
    ADD COLUMN IF NOT EXISTS ledger_version bigint;

WITH ranked AS (
    SELECT id,
           row_number() OVER (
               PARTITION BY user_id ORDER BY created_at, id) AS ledger_version
    FROM balance_ledger
)
UPDATE balance_ledger ledger
SET ledger_version = ranked.ledger_version
FROM ranked
WHERE ledger.id = ranked.id AND ledger.ledger_version IS NULL;

INSERT INTO accounting_accounts(user_id, posted_balance, ledger_version)
SELECT user_id, sum(amount), max(ledger_version)
FROM balance_ledger
GROUP BY user_id
ON CONFLICT (user_id) DO UPDATE
SET posted_balance = EXCLUDED.posted_balance,
    ledger_version = EXCLUDED.ledger_version,
    updated_at = now();

INSERT INTO accounting_accounts(user_id)
SELECT entity_id
FROM entity_registry
WHERE entity_type = 'user' AND status = 'active'
ON CONFLICT (user_id) DO NOTHING;

ALTER TABLE balance_ledger
    ALTER COLUMN ledger_version SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_balance_ledger_version_positive'
    ) THEN
        ALTER TABLE balance_ledger
            ADD CONSTRAINT ck_balance_ledger_version_positive
            CHECK (ledger_version > 0);
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_balance_ledger_user_version
    ON balance_ledger(user_id, ledger_version);

CREATE TABLE IF NOT EXISTS accounting_projection_outbox (
    user_id bigint PRIMARY KEY,
    ledger_version bigint NOT NULL CHECK (ledger_version > 0),
    posted_balance numeric(20,8) NOT NULL,
    attempts integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(),
    claimed_by text,
    claimed_until timestamptz,
    last_error text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_accounting_projection_claimable
    ON accounting_projection_outbox(next_attempt_at, user_id);

INSERT INTO accounting_projection_outbox(user_id, ledger_version, posted_balance)
SELECT user_id, ledger_version, posted_balance
FROM accounting_accounts
WHERE ledger_version > 0
ON CONFLICT (user_id) DO UPDATE
SET ledger_version = EXCLUDED.ledger_version,
    posted_balance = EXCLUDED.posted_balance,
    attempts = 0,
    next_attempt_at = now(),
    claimed_by = NULL,
    claimed_until = NULL,
    last_error = NULL,
    updated_at = now()
WHERE accounting_projection_outbox.ledger_version < EXCLUDED.ledger_version;
