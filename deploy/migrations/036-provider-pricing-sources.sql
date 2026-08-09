-- Provider price snapshots are source-owned inputs to the new pricing contract.
-- The version primary key remains the immutable replay boundary; a changed
-- snapshot gets a new checksum-derived version instead of rewriting history.
ALTER TABLE pricing_versions
    ADD COLUMN IF NOT EXISTS source_provider text NOT NULL DEFAULT 'admin',
    ADD COLUMN IF NOT EXISTS source_model text,
    ADD COLUMN IF NOT EXISTS source_checksum text;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_pricing_versions_source_provider'
          AND conrelid = 'pricing_versions'::regclass
    ) THEN
        ALTER TABLE pricing_versions
            ADD CONSTRAINT ck_pricing_versions_source_provider
            CHECK (length(source_provider) BETWEEN 1 AND 64);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_pricing_versions_source_model_effective
    ON pricing_versions(source_provider, model, effective_from DESC);
