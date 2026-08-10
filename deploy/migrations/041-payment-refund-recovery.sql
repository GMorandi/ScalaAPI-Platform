-- Refund commands are recovered by a hosted worker when the process dies
-- after Provider contact. Claims expire, and the Provider idempotency key makes
-- a retry safe even when the upstream already accepted the refund.
ALTER TABLE payment_refunds
    ADD COLUMN IF NOT EXISTS actor_user_id bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS attempts integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS last_attempt_at timestamptz,
    ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS claimed_by text,
    ADD COLUMN IF NOT EXISTS claimed_until timestamptz;

ALTER TABLE payment_refunds
    DROP CONSTRAINT IF EXISTS ck_payment_refunds_attempts;
ALTER TABLE payment_refunds
    ADD CONSTRAINT ck_payment_refunds_attempts CHECK (attempts >= 0 AND attempts <= 1000);

CREATE INDEX IF NOT EXISTS ix_payment_refunds_claimable
    ON payment_refunds(next_attempt_at, id)
    WHERE status IN ('pending', 'reconciliation_needed');
