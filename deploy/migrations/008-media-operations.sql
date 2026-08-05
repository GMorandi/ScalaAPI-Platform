-- Durable state for asynchronous image and video work.  Object bytes belong
-- in object storage; this table owns only metadata and provider task linkage.
CREATE TABLE IF NOT EXISTS media_operations (
    operation_id text PRIMARY KEY,
    idempotency_key text NOT NULL,
    operation_type text NOT NULL,
    status text NOT NULL CHECK (status IN ('pending', 'running', 'succeeded', 'failed', 'canceled', 'expired')),
    api_key_id bigint NOT NULL,
    account_id bigint NOT NULL,
    request_id text NOT NULL,
    lease_token text NOT NULL REFERENCES request_leases(lease_token),
    upstream_task_id text,
    progress integer NOT NULL DEFAULT 0 CHECK (progress BETWEEN 0 AND 100),
    output_metadata jsonb,
    error jsonb,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (api_key_id, idempotency_key)
);

CREATE INDEX IF NOT EXISTS ix_media_operations_active
    ON media_operations (status, expires_at, updated_at)
    WHERE status IN ('pending', 'running');
CREATE INDEX IF NOT EXISTS ix_media_operations_upstream_task
    ON media_operations (account_id, upstream_task_id)
    WHERE upstream_task_id IS NOT NULL;
