-- Channel monitor templates, checks, incidents, and OPS metrics samples
-- Supports automated channel monitoring with leader fencing, retry, and incident tracking.
-- OPS metrics samples support p95, error budgets, and retention.

CREATE TABLE IF NOT EXISTS channel_monitor_templates (
    template_id text PRIMARY KEY,
    name text NOT NULL,
    check_type text NOT NULL,
    schedule_cron text NOT NULL DEFAULT '*/5 * * * *',
    timeout_seconds integer NOT NULL DEFAULT 30,
    retry_count integer NOT NULL DEFAULT 3,
    alert_threshold integer NOT NULL DEFAULT 3,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS channel_monitor_checks (
    check_id bigserial PRIMARY KEY,
    template_id text NOT NULL,
    worker_id text NOT NULL,
    started_at timestamptz NOT NULL,
    completed_at timestamptz,
    status text NOT NULL DEFAULT 'pending',
    result jsonb,
    error_message text,
    leader_token text,
    UNIQUE(template_id, leader_token)
);

CREATE TABLE IF NOT EXISTS channel_monitor_incidents (
    incident_id bigserial PRIMARY KEY,
    template_id text NOT NULL,
    opened_at timestamptz NOT NULL DEFAULT now(),
    closed_at timestamptz,
    resolution text,
    check_count integer NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS ops_metrics_samples (
    sample_id bigserial PRIMARY KEY,
    metric_name text NOT NULL,
    labels jsonb NOT NULL,
    value numeric(20,8) NOT NULL,
    sampled_at timestamptz NOT NULL DEFAULT now(),
    request_id text,
    lease_id text
);
CREATE INDEX IF NOT EXISTS idx_ops_metrics_name_time ON ops_metrics_samples(metric_name, sampled_at DESC);
