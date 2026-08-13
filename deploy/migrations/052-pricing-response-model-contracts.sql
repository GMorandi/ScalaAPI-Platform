-- 052: Pricing/response-model contracts
-- Adds observed model tracking, new billing units (search, audio, character,
-- long-context), and model-mismatch detection for conservative billing.

-- request_leases: observed model + price source provenance + new price columns
ALTER TABLE request_leases
    ADD COLUMN IF NOT EXISTS observed_model text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS price_source_provider text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS price_source_checksum text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS price_search_per_query numeric(20,8),
    ADD COLUMN IF NOT EXISTS price_audio_per_minute numeric(20,8),
    ADD COLUMN IF NOT EXISTS price_character_per_million numeric(20,8),
    ADD COLUMN IF NOT EXISTS price_long_context_per_million numeric(20,8);

ALTER TABLE request_leases
    ADD CONSTRAINT ck_request_leases_new_price_snapshot_nonnegative
    CHECK (
        (price_search_per_query IS NULL OR price_search_per_query >= 0) AND
        (price_audio_per_minute IS NULL OR price_audio_per_minute >= 0) AND
        (price_character_per_million IS NULL OR price_character_per_million >= 0) AND
        (price_long_context_per_million IS NULL OR price_long_context_per_million >= 0)
    );

-- usage_events: new billing units + model mismatch tracking
ALTER TABLE usage_events
    ADD COLUMN IF NOT EXISTS observed_model text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS search_query_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS audio_minutes numeric(20,8) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS character_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS long_context_token_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS price_source_provider text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS price_source_checksum text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS model_mismatch_detected boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS model_mismatch_billing_model text NOT NULL DEFAULT '';

-- usage_logs: same new columns
ALTER TABLE usage_logs
    ADD COLUMN IF NOT EXISTS observed_model text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS search_query_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS audio_minutes numeric(20,8) NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS character_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS long_context_token_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS price_source_provider text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS price_source_checksum text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS model_mismatch_detected boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS model_mismatch_billing_model text NOT NULL DEFAULT '';

-- pricing_versions: new unit prices
ALTER TABLE pricing_versions
    ADD COLUMN IF NOT EXISTS search_per_query numeric(20,8) NOT NULL DEFAULT 0 CHECK (search_per_query >= 0),
    ADD COLUMN IF NOT EXISTS audio_per_minute numeric(20,8) NOT NULL DEFAULT 0 CHECK (audio_per_minute >= 0),
    ADD COLUMN IF NOT EXISTS character_per_million numeric(20,8) NOT NULL DEFAULT 0 CHECK (character_per_million >= 0),
    ADD COLUMN IF NOT EXISTS long_context_per_million numeric(20,8) NOT NULL DEFAULT 0 CHECK (long_context_per_million >= 0);
