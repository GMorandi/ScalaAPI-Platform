ALTER TABLE payment_orders
    ADD COLUMN IF NOT EXISTS provider_payment_id text;

ALTER TABLE payment_orders
    DROP CONSTRAINT IF EXISTS ck_payment_orders_provider_payment_id;

ALTER TABLE payment_orders
    ADD CONSTRAINT ck_payment_orders_provider_payment_id
    CHECK (provider_payment_id IS NULL OR length(provider_payment_id) BETWEEN 1 AND 128);

CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_orders_provider_payment
    ON payment_orders(provider, provider_payment_id)
    WHERE provider_payment_id IS NOT NULL;
