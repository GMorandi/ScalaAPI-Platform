-- OAuth authorization state is short-lived product state, never a compatibility
-- layer. Raw state and PKCE verifiers are returned only to the initiating client.
CREATE TABLE auth_oauth_states (
    state_hash text PRIMARY KEY CHECK (length(state_hash) = 64),
    provider text NOT NULL CHECK (provider IN ('github', 'google')),
    redirect_uri text NOT NULL CHECK (length(redirect_uri) BETWEEN 1 AND 2048),
    verifier_hash text NOT NULL CHECK (length(verifier_hash) = 64),
    expires_at timestamptz NOT NULL,
    consumed_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_auth_oauth_states_expiry
    ON auth_oauth_states(expires_at)
    WHERE consumed_at IS NULL;
