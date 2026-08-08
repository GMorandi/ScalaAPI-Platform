-- Atomic redemption effects are keyed by the product reference, not by a
-- nullable lease token. This prevents a retry or concurrent request from
-- creating a second bonus ledger entry.
CREATE UNIQUE INDEX IF NOT EXISTS ux_balance_ledger_reference_entry
    ON balance_ledger(reference, entry_type)
    WHERE reference IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_redeem_code_redemptions_user
    ON redeem_code_redemptions(user_id, created_at DESC);
