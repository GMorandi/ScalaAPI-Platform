-- Close the remaining SQLSugar projection gap and make CDC readiness evidence
-- monotonic across connector restarts and out-of-order topic partitions.

ALTER TABLE redeem_codes
    ADD COLUMN IF NOT EXISTS discount_amount numeric(20, 2) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS bonus_amount numeric(20, 8) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS max_uses integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS used_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS expires_at timestamptz;

ALTER TABLE cdc_checkpoints
    ADD COLUMN IF NOT EXISTS source_lsn_value numeric(39, 0),
    ADD COLUMN IF NOT EXISTS last_partition integer,
    ADD COLUMN IF NOT EXISTS last_offset bigint;

CREATE INDEX IF NOT EXISTS ix_cdc_checkpoints_snapshot
    ON cdc_checkpoints (snapshot_completed, updated_at DESC);
