-- Batch item objects are reconciled independently from the settled parent lease.
-- Claim fencing prevents a stale worker from overwriting a later repair result.
ALTER TABLE media_operation_items
    ADD COLUMN IF NOT EXISTS object_verified_at timestamptz,
    ADD COLUMN IF NOT EXISTS object_reconcile_attempts integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS object_next_check_at timestamptz;

ALTER TABLE media_operation_items
    DROP CONSTRAINT IF EXISTS ck_media_item_reconcile_attempts;

ALTER TABLE media_operation_items
    ADD CONSTRAINT ck_media_item_reconcile_attempts
    CHECK (object_reconcile_attempts >= 0);

CREATE INDEX IF NOT EXISTS ix_media_operation_items_reconcile
    ON media_operation_items (object_next_check_at, updated_at)
    WHERE object_status IN ('pending', 'stored', 'failed');
