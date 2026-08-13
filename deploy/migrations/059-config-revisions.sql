CREATE TABLE IF NOT EXISTS config_revisions (
    revision_id bigserial PRIMARY KEY,
    config_key text NOT NULL,
    config_value text NOT NULL,
    previous_revision_id bigint,
    actor_user_id bigint,
    actor_reason text,
    created_at timestamptz NOT NULL DEFAULT now(),
    applied_at timestamptz,
    rolled_back_at timestamptz,
    status text NOT NULL DEFAULT 'pending'
);
CREATE INDEX IF NOT EXISTS idx_config_revisions_key ON config_revisions(config_key, created_at DESC);

CREATE TABLE IF NOT EXISTS config_node_observations (
    node_id text NOT NULL,
    last_seen_revision bigint NOT NULL,
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (node_id)
);
