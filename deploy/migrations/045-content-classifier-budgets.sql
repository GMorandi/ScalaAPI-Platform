-- Durable classifier budget state. This is operational evidence only: it
-- contains numeric observations and fixed classifier/budget names, never
-- request content, policy text, endpoints, or credentials.
CREATE TABLE IF NOT EXISTS content_classifier_budget_alerts (
    event_key text PRIMARY KEY,
    classifier text NOT NULL CHECK (classifier = 'openai'),
    budget_kind text NOT NULL CHECK (budget_kind IN ('unavailable_ratio', 'p95_latency')),
    status text NOT NULL CHECK (status IN ('open', 'resolved')),
    observed_value numeric(20,8) NOT NULL CHECK (observed_value >= 0),
    threshold_value numeric(20,8) NOT NULL CHECK (threshold_value >= 0),
    sample_count bigint NOT NULL CHECK (sample_count >= 0),
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    resolved_at timestamptz
);
CREATE INDEX IF NOT EXISTS ix_classifier_budget_alert_status
    ON content_classifier_budget_alerts(status, last_seen_at DESC);
