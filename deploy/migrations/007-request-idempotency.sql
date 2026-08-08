-- Product-native idempotency for synchronous and streaming protocol requests.
-- Media operations keep their richer lifecycle in media_operations.
CREATE TABLE IF NOT EXISTS request_idempotency (
    api_key_id bigint NOT NULL,
    idempotency_key text NOT NULL,
    request_fingerprint text NOT NULL,
    request_id text NOT NULL,
    lease_token text NOT NULL REFERENCES request_leases(lease_token) ON DELETE CASCADE,
    status text NOT NULL CHECK (status IN ('active', 'completed', 'aborted', 'expired')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (api_key_id, idempotency_key),
    UNIQUE (lease_token)
);

CREATE INDEX IF NOT EXISTS ix_request_idempotency_status
    ON request_idempotency(status, updated_at);
