-- Refunds are independent provider/accounting commands. Multiple successful
-- partial refunds are allowed, while only one unresolved command may run for
-- an order at a time.
ALTER TABLE payment_orders
    ADD COLUMN IF NOT EXISTS refunded_amount numeric(20,2) NOT NULL DEFAULT 0;

ALTER TABLE payment_orders
    DROP CONSTRAINT IF EXISTS ck_payment_orders_refunded_amount;
ALTER TABLE payment_orders
    ADD CONSTRAINT ck_payment_orders_refunded_amount
    CHECK (refunded_amount >= 0 AND refunded_amount <= amount);

DROP INDEX IF EXISTS ux_payment_refunds_order_active;
CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_refunds_order_active
    ON payment_refunds(payment_order_id)
    WHERE status IN ('pending', 'reconciliation_needed');
