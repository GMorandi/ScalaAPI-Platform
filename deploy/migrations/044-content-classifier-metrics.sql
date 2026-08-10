-- Greenfield operational snapshots for cross-process content classifier metrics.
-- The table deliberately contains only fixed classifier identity and numeric
-- counters; request content, rule text, endpoint, and credentials never enter
-- the metrics path.
CREATE TABLE IF NOT EXISTS content_classifier_metric_snapshots (
    instance_id uuid NOT NULL,
    sequence bigint NOT NULL CHECK (sequence > 0),
    classifier text NOT NULL CHECK (classifier = 'openai'),
    requests bigint NOT NULL CHECK (requests >= 0),
    matches bigint NOT NULL CHECK (matches >= 0),
    no_matches bigint NOT NULL CHECK (no_matches >= 0),
    unavailable bigint NOT NULL CHECK (unavailable >= 0),
    protocol_errors bigint NOT NULL CHECK (protocol_errors >= 0),
    cancellations bigint NOT NULL CHECK (cancellations >= 0),
    duration_ticks bigint NOT NULL CHECK (duration_ticks >= 0),
    bucket_0 bigint NOT NULL CHECK (bucket_0 >= 0),
    bucket_1 bigint NOT NULL CHECK (bucket_1 >= 0),
    bucket_2 bigint NOT NULL CHECK (bucket_2 >= 0),
    bucket_3 bigint NOT NULL CHECK (bucket_3 >= 0),
    bucket_4 bigint NOT NULL CHECK (bucket_4 >= 0),
    bucket_5 bigint NOT NULL CHECK (bucket_5 >= 0),
    bucket_6 bigint NOT NULL CHECK (bucket_6 >= 0),
    bucket_7 bigint NOT NULL CHECK (bucket_7 >= 0),
    bucket_8 bigint NOT NULL CHECK (bucket_8 >= 0),
    bucket_9 bigint NOT NULL CHECK (bucket_9 >= 0),
    captured_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (instance_id, sequence)
);
CREATE INDEX IF NOT EXISTS ix_classifier_metric_snapshots_classifier_captured
    ON content_classifier_metric_snapshots(classifier, captured_at);
