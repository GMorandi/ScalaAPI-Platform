-- Idempotent, auditable maintenance commands for the greenfield product.
CREATE TABLE IF NOT EXISTS maintenance_operations (
    operation_key text PRIMARY KEY,
    actor_user_id bigint NOT NULL,
    request_fingerprint text NOT NULL,
    dry_run boolean NOT NULL,
    result jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_maintenance_operations_actor_created
    ON maintenance_operations(actor_user_id, created_at DESC);
