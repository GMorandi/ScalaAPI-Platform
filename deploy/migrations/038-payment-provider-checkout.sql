ALTER TABLE payment_orders
    ADD COLUMN IF NOT EXISTS checkout_url text;

ALTER TABLE payment_orders
    DROP CONSTRAINT IF EXISTS ck_payment_orders_checkout_url;

ALTER TABLE payment_orders
    ADD CONSTRAINT ck_payment_orders_checkout_url
    CHECK (checkout_url IS NULL OR length(checkout_url) <= 2048);
