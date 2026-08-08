ALTER TABLE media_operations
    ADD COLUMN IF NOT EXISTS object_key text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS object_etag text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS object_size bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS object_status text NOT NULL DEFAULT 'none',
    ADD COLUMN IF NOT EXISTS object_error jsonb;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'media_operations_object_status_check'
    ) THEN
        ALTER TABLE media_operations
            ADD CONSTRAINT media_operations_object_status_check
            CHECK (object_status IN ('none', 'pending', 'stored', 'failed', 'deleted'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_media_operations_object_status
    ON media_operations (object_status, updated_at)
    WHERE object_status IN ('pending', 'failed');
