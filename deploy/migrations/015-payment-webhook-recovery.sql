ALTER TABLE payment_webhook_events
    ADD COLUMN IF NOT EXISTS attempts integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS next_attempt_at timestamptz,
    ADD COLUMN IF NOT EXISTS last_attempt_at timestamptz;

CREATE INDEX IF NOT EXISTS ix_payment_webhook_events_recovery
    ON payment_webhook_events(next_attempt_at, id)
    WHERE status = 'pending';
