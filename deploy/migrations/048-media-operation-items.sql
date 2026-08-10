-- Batch item bytes have their own durable ownership and download projection.
-- The parent media operation remains the billing and retention authority.
CREATE TABLE IF NOT EXISTS media_operation_items (
    item_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    operation_id text NOT NULL REFERENCES media_operations(operation_id) ON DELETE CASCADE,
    item_index integer NOT NULL CHECK (item_index >= 0),
    custom_id text NOT NULL CHECK (char_length(custom_id) BETWEEN 1 AND 256),
    provider_url text NOT NULL DEFAULT '' CHECK (char_length(provider_url) <= 8192),
    object_key text NOT NULL DEFAULT '' CHECK (char_length(object_key) <= 1024),
    object_etag text NOT NULL DEFAULT '' CHECK (char_length(object_etag) <= 256),
    object_size bigint NOT NULL DEFAULT 0 CHECK (object_size >= 0),
    content_type text NOT NULL DEFAULT 'application/octet-stream'
        CHECK (char_length(content_type) BETWEEN 1 AND 256),
    object_status text NOT NULL DEFAULT 'pending'
        CHECK (object_status IN ('pending', 'stored', 'failed', 'deleted')),
    output_url text NOT NULL DEFAULT '' CHECK (char_length(output_url) <= 8192),
    error jsonb,
    retention_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (operation_id, item_index)
);

CREATE INDEX IF NOT EXISTS ix_media_operation_items_status
    ON media_operation_items (object_status, updated_at)
    WHERE object_status IN ('pending', 'failed');

CREATE INDEX IF NOT EXISTS ix_media_operation_items_retention
    ON media_operation_items (retention_until, updated_at)
    WHERE object_status IN ('stored', 'failed') AND object_key <> '';
