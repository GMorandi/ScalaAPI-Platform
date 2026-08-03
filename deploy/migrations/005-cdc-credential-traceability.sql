-- Preserve ordering and operation metadata for the restricted credential
-- channel. The ciphertext remains opaque to the ordinary CDC consumer.
ALTER TABLE cdc_credential_payloads
    ADD COLUMN IF NOT EXISTS source_lsn text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS transaction_id text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS operation text NOT NULL DEFAULT 'update',
    ADD COLUMN IF NOT EXISTS occurred_at timestamptz NOT NULL DEFAULT now();

ALTER TABLE cdc_credential_payloads
    DROP CONSTRAINT IF EXISTS ck_cdc_credential_payloads_operation;

ALTER TABLE cdc_credential_payloads
    ADD CONSTRAINT ck_cdc_credential_payloads_operation
    CHECK (operation IN ('insert', 'update', 'delete', 'snapshot'));

CREATE INDEX IF NOT EXISTS ix_cdc_credential_payloads_order
    ON cdc_credential_payloads (epoch, source_lsn, created_at);
