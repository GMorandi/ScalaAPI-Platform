-- 063-security-hardening.sql
-- Master-key rotation log, immutable audit log, TLS certificate tracker

CREATE TABLE IF NOT EXISTS secret_rotation_log (
    rotation_id bigserial PRIMARY KEY,
    old_key_id text NOT NULL,
    new_key_id text NOT NULL,
    rotated_at timestamptz NOT NULL DEFAULT now(),
    rotated_by bigint,
    status text NOT NULL DEFAULT 'in_progress'
);

CREATE TABLE IF NOT EXISTS audit_log_immutable (
    log_id bigserial PRIMARY KEY,
    event_type text NOT NULL,
    actor_user_id bigint,
    actor_ip text,
    resource_type text,
    resource_id text,
    action text NOT NULL,
    result text NOT NULL,
    details jsonb,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_audit_log_time ON audit_log_immutable(created_at DESC);

-- Prevent UPDATE and DELETE on the immutable audit log
CREATE OR REPLACE FUNCTION reject_audit_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'audit_log_immutable is append-only; mutations are forbidden';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_audit_immutable_no_update ON audit_log_immutable;
CREATE TRIGGER trg_audit_immutable_no_update
    BEFORE UPDATE ON audit_log_immutable
    FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();

DROP TRIGGER IF EXISTS trg_audit_immutable_no_delete ON audit_log_immutable;
CREATE TRIGGER trg_audit_immutable_no_delete
    BEFORE DELETE ON audit_log_immutable
    FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();

CREATE TABLE IF NOT EXISTS tls_certificate_tracker (
    cert_id text PRIMARY KEY,
    subject text NOT NULL,
    issuer text,
    not_before timestamptz NOT NULL,
    not_after timestamptz NOT NULL,
    last_checked_at timestamptz,
    status text NOT NULL DEFAULT 'unknown'
);
