-- Greenfield authentication abuse controls. Keys are one-way digests of the
-- email/IP dimensions; no public identifier is stored in the counter table.
CREATE TABLE IF NOT EXISTS auth_abuse_counters (
    counter_key text PRIMARY KEY,
    failure_count integer NOT NULL DEFAULT 0,
    window_started_at timestamptz NOT NULL DEFAULT now(),
    locked_until timestamptz,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_auth_abuse_counters_locked
    ON auth_abuse_counters(locked_until)
    WHERE locked_until IS NOT NULL;
