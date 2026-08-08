-- Administrative funding is an idempotent accounting effect. PostgreSQL owns
-- the amount, actor, reason, and request identity; Orleans only projects the
-- resulting authoritative balance.
ALTER TABLE balance_ledger
    ADD COLUMN IF NOT EXISTS idempotency_key text,
    ADD COLUMN IF NOT EXISTS description text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS created_by bigint;

CREATE UNIQUE INDEX IF NOT EXISTS ux_balance_ledger_user_idempotency_entry
    ON balance_ledger(user_id, idempotency_key, entry_type)
    WHERE idempotency_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_balance_ledger_user_created
    ON balance_ledger(user_id, created_at DESC, id DESC);
