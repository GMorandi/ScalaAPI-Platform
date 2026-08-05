-- Append-only accounting metadata. Monetary settlement remains decimal and is
-- calculated from configured pricing; unknown media prices are never guessed.
ALTER TABLE usage_events
    ADD COLUMN IF NOT EXISTS input_image_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS output_image_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS image_size text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS video_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS video_resolution text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS video_duration_seconds integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS realtime_duration_ms integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS realtime_frames integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS disconnect_reason text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS provider_usage_json text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS reasoning_tokens integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS service_tier text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS upstream_endpoint text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS cancellation_reason text NOT NULL DEFAULT '';
ALTER TABLE usage_events
    ADD COLUMN IF NOT EXISTS media_operation_id text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS pricing_version text NOT NULL DEFAULT '';

ALTER TABLE usage_logs
    ADD COLUMN IF NOT EXISTS input_image_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS output_image_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS image_size text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS video_count integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS video_resolution text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS video_duration_seconds integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS realtime_duration_ms integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS realtime_frames integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS disconnect_reason text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS provider_usage_json text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS reasoning_tokens integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS service_tier text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS upstream_endpoint text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS cancellation_reason text NOT NULL DEFAULT '';
ALTER TABLE usage_logs
    ADD COLUMN IF NOT EXISTS media_operation_id text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS pricing_version text NOT NULL DEFAULT '';
