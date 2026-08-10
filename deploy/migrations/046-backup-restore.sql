-- Greenfield backup artifacts are explicit, idempotent jobs. The artifact path
-- points at a dedicated backup volume; credentials and connection strings are
-- configuration-only and never enter these rows.
CREATE TABLE IF NOT EXISTS backup_jobs (
    id text PRIMARY KEY,
    kind text NOT NULL CHECK (kind IN ('postgres', 'full')),
    idempotency_key text NOT NULL UNIQUE,
    request_fingerprint text NOT NULL,
    status text NOT NULL CHECK (status IN ('running', 'completed', 'failed')),
    artifact_path text,
    size_bytes bigint,
    sha256 text,
    retention_until timestamptz,
    created_by bigint NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    error_code text,
    error_detail text,
    CHECK (size_bytes IS NULL OR size_bytes >= 0),
    CHECK (sha256 IS NULL OR sha256 ~ '^[0-9a-f]{64}$')
);

CREATE INDEX IF NOT EXISTS ix_backup_jobs_status_created
    ON backup_jobs(status, created_at DESC);

CREATE TABLE IF NOT EXISTS backup_restore_runs (
    id text PRIMARY KEY,
    backup_id text NOT NULL REFERENCES backup_jobs(id),
    idempotency_key text NOT NULL UNIQUE,
    request_fingerprint text NOT NULL,
    status text NOT NULL CHECK (status IN ('running', 'completed', 'failed')),
    target_fingerprint text NOT NULL,
    created_by bigint NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    error_code text,
    error_detail text
);

CREATE INDEX IF NOT EXISTS ix_backup_restore_runs_backup_created
    ON backup_restore_runs(backup_id, created_at DESC);
