-- Passive Channel Monitor V2: watermark/backfill, multi-dimensional rollups,
-- latency histograms, privacy defaults, retention, and leader lock support.

CREATE TABLE IF NOT EXISTS monitor_v2_watermarks (
    dimension text PRIMARY KEY,
    watermark_event_id bigint NOT NULL DEFAULT 0,
    watermark_timestamp timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS monitor_v2_rollups (
    rollup_id bigserial PRIMARY KEY,
    dimension text NOT NULL,
    dimension_value text NOT NULL,
    window_start timestamptz NOT NULL,
    window_end timestamptz NOT NULL,
    event_count integer NOT NULL DEFAULT 0,
    error_count integer NOT NULL DEFAULT 0,
    latency_p50 numeric(10,2),
    latency_p95 numeric(10,2),
    latency_p99 numeric(10,2),
    unique_event_ids bigint[] NOT NULL DEFAULT '{}',
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(dimension, dimension_value, window_start)
);
CREATE INDEX IF NOT EXISTS idx_monitor_v2_rollups_dim_time ON monitor_v2_rollups(dimension, window_start DESC);

CREATE TABLE IF NOT EXISTS monitor_v2_privacy_config (
    config_key text PRIMARY KEY,
    redact_user_ids boolean NOT NULL DEFAULT true,
    redact_prompts boolean NOT NULL DEFAULT true,
    retention_days integer NOT NULL DEFAULT 90,
    updated_at timestamptz NOT NULL DEFAULT now()
);

-- Insert default privacy config if not present
INSERT INTO monitor_v2_privacy_config (config_key, redact_user_ids, redact_prompts, retention_days)
VALUES ('default', true, true, 90)
ON CONFLICT (config_key) DO NOTHING;
