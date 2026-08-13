-- Payment state machine: add refund tracking and webhook idempotency columns
-- to the payment_orders table. These support provider-authoritative payment
-- completion and refund accumulation tracking.

ALTER TABLE payment_orders ADD COLUMN IF NOT EXISTS refund_total numeric(20,8) NOT NULL DEFAULT 0;

ALTER TABLE payment_orders ADD COLUMN IF NOT EXISTS webhook_idempotency_key text;

CREATE INDEX IF NOT EXISTS idx_payment_orders_webhook
    ON payment_orders(webhook_idempotency_key)
    WHERE webhook_idempotency_key IS NOT NULL;
