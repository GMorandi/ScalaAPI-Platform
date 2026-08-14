-- 055: Search history and audit trail
-- Records each search request with query, filters, result metadata,
-- provider/account provenance, and outcome for user history and admin audit.

CREATE TABLE IF NOT EXISTS search_history (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id bigint NOT NULL,
    api_key_id bigint NOT NULL,
    lease_id text NOT NULL,
    query text NOT NULL,
    domain_filter text,
    recency_filter text,
    result_count integer NOT NULL DEFAULT 0,
    truncated boolean NOT NULL DEFAULT false,
    provider_platform text NOT NULL,
    provider_account_id bigint NOT NULL,
    status text NOT NULL DEFAULT 'success',
    error_code text,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT fk_search_history_user FOREIGN KEY (user_id) REFERENCES user_accounts(id),
    CONSTRAINT fk_search_history_api_key FOREIGN KEY (api_key_id) REFERENCES user_api_keys(id),
    CONSTRAINT uq_search_history_lease UNIQUE (lease_id)
);
CREATE INDEX IF NOT EXISTS idx_search_history_user_created ON search_history(user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_search_history_provider_status ON search_history(provider_platform, status);
