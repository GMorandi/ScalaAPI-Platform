-- Object metadata is reconciled independently from the media operation state.
-- A missing object is repairable and must not rewrite an already settled lease.
ALTER TABLE media_operations
    ADD COLUMN IF NOT EXISTS object_verified_at timestamptz,
    ADD COLUMN IF NOT EXISTS object_reconcile_attempts integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS object_next_check_at timestamptz;

ALTER TABLE media_operations
    DROP CONSTRAINT IF EXISTS ck_media_object_reconcile_attempts;

ALTER TABLE media_operations
    ADD CONSTRAINT ck_media_object_reconcile_attempts
    CHECK (object_reconcile_attempts >= 0);

CREATE INDEX IF NOT EXISTS ix_media_operations_object_reconcile
    ON media_operations (object_next_check_at, updated_at)
    WHERE status = 'succeeded' AND object_status IN ('stored', 'failed')
      AND object_key <> '';
