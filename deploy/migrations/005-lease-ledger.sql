-- Durable usage debits are keyed by the completed lease, independently from
-- payment rows. This keeps lease retries idempotent without reusing legacy
-- payment identifiers.
ALTER TABLE balance_ledger
    ADD COLUMN IF NOT EXISTS lease_token text,
    ADD COLUMN IF NOT EXISTS entry_type text NOT NULL DEFAULT 'manual';

DROP INDEX IF EXISTS ux_balance_ledger_lease_entry;
CREATE UNIQUE INDEX IF NOT EXISTS ux_balance_ledger_lease_entry
    ON balance_ledger(lease_token, entry_type);
