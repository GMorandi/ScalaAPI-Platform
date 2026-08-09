-- Request and response policy decisions have different delivery and billing
-- semantics. Persist the stage explicitly and make repeated RPC evaluation
-- idempotent for one rule/request/stage tuple.
ALTER TABLE content_audit_rules
    ADD COLUMN IF NOT EXISTS stage text NOT NULL DEFAULT 'request';

ALTER TABLE content_audit_logs
    ADD COLUMN IF NOT EXISTS rule_id bigint REFERENCES content_audit_rules(id)
        ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS stage text NOT NULL DEFAULT 'request';

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_content_audit_rules_stage'
    ) THEN
        ALTER TABLE content_audit_rules
            ADD CONSTRAINT ck_content_audit_rules_stage
            CHECK (stage IN ('request', 'response', 'both'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_content_audit_logs_stage'
    ) THEN
        ALTER TABLE content_audit_logs
            ADD CONSTRAINT ck_content_audit_logs_stage
            CHECK (stage IN ('request', 'response'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_content_audit_rules_active_stage_scope
    ON content_audit_rules(stage, scope, id)
    WHERE status = 'active';

CREATE UNIQUE INDEX IF NOT EXISTS ux_content_audit_logs_request_rule_stage
    ON content_audit_logs(request_id, rule_id, stage)
    WHERE request_id IS NOT NULL AND rule_id IS NOT NULL;
