ALTER TABLE media_operations
    ADD COLUMN IF NOT EXISTS request_fingerprint text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS provider text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS output_url text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS content_type text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS cancel_requested boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS attempts integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS next_poll_at timestamptz,
    ADD COLUMN IF NOT EXISTS last_polled_at timestamptz;

CREATE INDEX IF NOT EXISTS ix_media_operations_poll
    ON media_operations (next_poll_at, updated_at)
    WHERE status IN ('pending', 'running');
