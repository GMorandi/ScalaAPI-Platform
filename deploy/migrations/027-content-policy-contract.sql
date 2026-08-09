-- Native content policy contract. Rules are bounded, explicit decisions; an
-- unknown action or status must never silently change runtime behaviour.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_content_audit_rules_action_type'
    ) THEN
        ALTER TABLE content_audit_rules
            ADD CONSTRAINT ck_content_audit_rules_action_type
            CHECK (action_type IN ('log', 'block'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_content_audit_rules_status'
    ) THEN
        ALTER TABLE content_audit_rules
            ADD CONSTRAINT ck_content_audit_rules_status
            CHECK (status IN ('active', 'disabled'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_content_audit_rules_pattern_length'
    ) THEN
        ALTER TABLE content_audit_rules
            ADD CONSTRAINT ck_content_audit_rules_pattern_length
            CHECK (char_length(pattern) BETWEEN 1 AND 512);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_content_audit_rules_active_scope
    ON content_audit_rules(scope, id)
    WHERE status = 'active';
