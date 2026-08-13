-- REL-02: Enhanced backup/restore with encryption, signing, offsite, retention policies,
-- cluster-singleton scheduling, and RPO/RTO tracking.

-- Retention policies for backup lifecycle management.
CREATE TABLE IF NOT EXISTS backup_retention_policies (
    policy_id text PRIMARY KEY DEFAULT 'default',
    keep_daily integer NOT NULL DEFAULT 7,
    keep_weekly integer NOT NULL DEFAULT 4,
    keep_monthly integer NOT NULL DEFAULT 12,
    offsite_enabled boolean NOT NULL DEFAULT false,
    offsite_url text,
    offsite_bucket text,
    offsite_region text,
    encryption_enabled boolean NOT NULL DEFAULT false,
    signing_enabled boolean NOT NULL DEFAULT false,
    encryption_key_id text,
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (keep_daily >= 1 AND keep_daily <= 365),
    CHECK (keep_weekly >= 0 AND keep_weekly <= 52),
    CHECK (keep_monthly >= 0 AND keep_monthly <= 120)
);

-- Encryption/signing key registry for backup artifact protection.
CREATE TABLE IF NOT EXISTS backup_signing_keys (
    key_id text PRIMARY KEY,
    algorithm text NOT NULL DEFAULT 'hmac-sha256'
        CHECK (algorithm IN ('hmac-sha256', 'aes-256-gcm', 'ed25519')),
    key_material bytea NOT NULL,
    status text NOT NULL DEFAULT 'active'
        CHECK (status IN ('active', 'rotating', 'retired')),
    created_at timestamptz NOT NULL DEFAULT now(),
    retired_at timestamptz,
    CHECK (key_id ~ '^[a-z0-9_-]{3,64}$')
);

-- Offsite upload tracking for backup artifacts.
CREATE TABLE IF NOT EXISTS backup_offsite_uploads (
    upload_id text PRIMARY KEY,
    backup_id text NOT NULL REFERENCES backup_jobs(id),
    provider text NOT NULL DEFAULT 's3' CHECK (provider IN ('s3', 'gcs', 'azure')),
    remote_url text NOT NULL,
    remote_checksum text,
    status text NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'uploading', 'completed', 'failed')),
    size_bytes bigint,
    started_at timestamptz,
    completed_at timestamptz,
    error_message text,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_backup_offsite_uploads_backup
    ON backup_offsite_uploads(backup_id);

-- Cluster-singleton backup schedule claims.
CREATE TABLE IF NOT EXISTS backup_schedule_claims (
    claim_id text PRIMARY KEY,
    schedule_key text NOT NULL UNIQUE,
    worker_id text NOT NULL,
    claimed_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    last_run_at timestamptz,
    last_run_status text,
    CHECK (expires_at > claimed_at)
);

-- RPO/RTO measurement records.
CREATE TABLE IF NOT EXISTS backup_rpo_rto_records (
    record_id text PRIMARY KEY,
    backup_id text REFERENCES backup_jobs(id),
    measured_at timestamptz NOT NULL DEFAULT now(),
    rpo_seconds double precision,
    rto_seconds double precision,
    backup_duration_seconds double precision,
    restore_duration_seconds double precision,
    verification_passed boolean NOT NULL DEFAULT false,
    details jsonb
);

CREATE INDEX IF NOT EXISTS ix_backup_rpo_rto_measured
    ON backup_rpo_rto_records(measured_at DESC);

-- Seed default retention policy if not present.
INSERT INTO backup_retention_policies (policy_id)
VALUES ('default')
ON CONFLICT (policy_id) DO NOTHING;
