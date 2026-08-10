-- Terminal media outputs have an independent retention deadline. Operation
-- expiry still controls provider polling; retention controls stored bytes.
ALTER TABLE media_operations
    ADD COLUMN IF NOT EXISTS retention_until timestamptz;

UPDATE media_operations
SET retention_until = expires_at
WHERE retention_until IS NULL;

CREATE INDEX IF NOT EXISTS ix_media_operations_retention
    ON media_operations (retention_until, updated_at)
    WHERE status IN ('succeeded', 'failed', 'canceled', 'expired')
      AND object_status IN ('stored', 'failed')
      AND object_key <> '';
