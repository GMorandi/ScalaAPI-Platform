-- 056: Voice CRUD and audio history
-- Supports custom voice management (user-scoped) and audio request audit trail.

-- Custom voices owned by users.
CREATE TABLE IF NOT EXISTS voices (
    id              bigserial PRIMARY KEY,
    user_id         bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    name            text NOT NULL,
    description     text NOT NULL DEFAULT '',
    voice_type      text NOT NULL DEFAULT 'custom' CHECK (voice_type IN ('custom', 'prebuilt')),
    audio_url       text NOT NULL DEFAULT '',
    metadata_json   text NOT NULL DEFAULT '{}',
    status          text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'archived', 'failed')),
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_voices_name_length CHECK (char_length(name) BETWEEN 1 AND 128),
    CONSTRAINT uq_voices_user_name UNIQUE (user_id, name)
);

CREATE INDEX IF NOT EXISTS idx_voices_user_status ON voices (user_id, status);

-- Audio request history for audit and billing reconciliation.
CREATE TABLE IF NOT EXISTS audio_history (
    id                  bigserial PRIMARY KEY,
    user_id             bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    api_key_id          bigint NOT NULL REFERENCES api_keys(id) ON DELETE CASCADE,
    lease_id            text NOT NULL,
    audio_type          text NOT NULL CHECK (audio_type IN ('tts', 'stt')),
    model               text NOT NULL DEFAULT '',
    voice               text NOT NULL DEFAULT '',
    input_length        integer NOT NULL DEFAULT 0,
    output_duration_sec numeric(20,8) NOT NULL DEFAULT 0,
    response_format     text NOT NULL DEFAULT '',
    language            text NOT NULL DEFAULT '',
    result_count        integer NOT NULL DEFAULT 0,
    provider_platform   text NOT NULL DEFAULT '',
    provider_account_id bigint NOT NULL DEFAULT 0,
    status              text NOT NULL DEFAULT 'pending',
    error_code          text,
    created_at          timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS uq_audio_history_lease_id ON audio_history (lease_id);
CREATE INDEX IF NOT EXISTS idx_audio_history_user_created ON audio_history (user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_audio_history_provider_status ON audio_history (provider_platform, status);
