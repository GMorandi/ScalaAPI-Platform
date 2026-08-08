-- A lease captures the exact price used for settlement. Later configuration
-- changes or process restarts must never reprice an already-forwarded request.
ALTER TABLE request_leases
    ADD COLUMN IF NOT EXISTS pricing_version text,
    ADD COLUMN IF NOT EXISTS price_input_per_million numeric(20,8),
    ADD COLUMN IF NOT EXISTS price_output_per_million numeric(20,8),
    ADD COLUMN IF NOT EXISTS price_cache_create_per_million numeric(20,8),
    ADD COLUMN IF NOT EXISTS price_cache_read_per_million numeric(20,8),
    ADD COLUMN IF NOT EXISTS price_image_input_per_unit numeric(20,8),
    ADD COLUMN IF NOT EXISTS price_image_output_per_unit numeric(20,8),
    ADD COLUMN IF NOT EXISTS price_video_per_second numeric(20,8),
    ADD COLUMN IF NOT EXISTS price_realtime_per_minute numeric(20,8);

ALTER TABLE request_leases
    ADD CONSTRAINT ck_request_leases_price_snapshot_nonnegative
    CHECK (
        (price_input_per_million IS NULL OR price_input_per_million >= 0) AND
        (price_output_per_million IS NULL OR price_output_per_million >= 0) AND
        (price_cache_create_per_million IS NULL OR price_cache_create_per_million >= 0) AND
        (price_cache_read_per_million IS NULL OR price_cache_read_per_million >= 0) AND
        (price_image_input_per_unit IS NULL OR price_image_input_per_unit >= 0) AND
        (price_image_output_per_unit IS NULL OR price_image_output_per_unit >= 0) AND
        (price_video_per_second IS NULL OR price_video_per_second >= 0) AND
        (price_realtime_per_minute IS NULL OR price_realtime_per_minute >= 0)
    );
