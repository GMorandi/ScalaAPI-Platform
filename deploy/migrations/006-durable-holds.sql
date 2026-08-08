-- Every request lease owns at most one durable balance reservation.  The
-- reservation is finalized in the same transaction as lease settlement.
CREATE UNIQUE INDEX IF NOT EXISTS ux_balance_holds_lease_token
    ON balance_holds(lease_token)
    WHERE lease_token IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_balance_holds_user_status
    ON balance_holds(user_id, status, created_at DESC);
