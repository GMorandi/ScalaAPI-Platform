-- Retention policies: immutable rules governing how long data categories are kept.
-- Once inserted, policies are never deleted; updates create new revisions.
CREATE TABLE IF NOT EXISTS retention_policies (
    policy_id bigserial PRIMARY KEY,
    category text NOT NULL,
    retention_days integer NOT NULL,
    description text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(category, created_at)
);
CREATE INDEX IF NOT EXISTS ix_retention_policies_category
    ON retention_policies(category, created_at DESC);

-- Export jobs: tracks user data export requests with bounded artifacts.
-- A job progresses: pending -> generating -> ready -> expired/downloaded.
-- download_token is a short-lived HMAC for authorized download.
CREATE TABLE IF NOT EXISTS export_jobs (
    job_id bigserial PRIMARY KEY,
    user_id bigint NOT NULL,
    status text NOT NULL DEFAULT 'pending',
    request_fingerprint text NOT NULL,
    artifact_key text,
    artifact_size_bytes bigint,
    artifact_hash text,
    download_token text,
    download_token_expires_at timestamptz,
    download_count integer NOT NULL DEFAULT 0,
    max_downloads integer NOT NULL DEFAULT 3,
    expires_at timestamptz NOT NULL,
    error text,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(user_id, request_fingerprint)
);
CREATE INDEX IF NOT EXISTS ix_export_jobs_user
    ON export_jobs(user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_export_jobs_status
    ON export_jobs(status) WHERE status IN ('pending', 'generating');
CREATE INDEX IF NOT EXISTS ix_export_jobs_expiry
    ON export_jobs(expires_at) WHERE status = 'ready';

-- Cleanup runs: audit trail for retention cleanup executions.
-- Records both dry-run and applied cleanups with per-category counts.
CREATE TABLE IF NOT EXISTS cleanup_runs (
    run_id bigserial PRIMARY KEY,
    actor_user_id bigint,
    idempotency_key text NOT NULL,
    request_fingerprint text NOT NULL,
    dry_run boolean NOT NULL DEFAULT false,
    status text NOT NULL DEFAULT 'running',
    total_deleted integer NOT NULL DEFAULT 0,
    total_failed integer NOT NULL DEFAULT 0,
    categories jsonb NOT NULL DEFAULT '{}'::jsonb,
    started_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz,
    error text,
    UNIQUE(idempotency_key)
);
CREATE INDEX IF NOT EXISTS ix_cleanup_runs_status
    ON cleanup_runs(status) WHERE status = 'running';
CREATE INDEX IF NOT EXISTS ix_cleanup_runs_completed
    ON cleanup_runs(completed_at DESC) WHERE status = 'completed';
