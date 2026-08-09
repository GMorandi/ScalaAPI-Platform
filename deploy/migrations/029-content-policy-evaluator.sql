-- Versioned Unicode normalization, classifier selection, audit redaction, and
-- a monotonic revision make policy changes observable across Platform instances.
ALTER TABLE content_audit_rules
    ADD COLUMN IF NOT EXISTS evaluator_version text NOT NULL DEFAULT 'unicode-confusable-v1',
    ADD COLUMN IF NOT EXISTS classifier text NOT NULL DEFAULT 'local',
    ADD COLUMN IF NOT EXISTS redact_content boolean NOT NULL DEFAULT false;

ALTER TABLE content_audit_logs
    ADD COLUMN IF NOT EXISTS evaluator_version text NOT NULL DEFAULT 'unicode-confusable-v1',
    ADD COLUMN IF NOT EXISTS classifier text NOT NULL DEFAULT 'local',
    ADD COLUMN IF NOT EXISTS content_redacted boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS policy_revision bigint NOT NULL DEFAULT 1;

CREATE TABLE IF NOT EXISTS content_policy_state (
    id smallint PRIMARY KEY CHECK (id = 1),
    revision bigint NOT NULL CHECK (revision > 0),
    evaluator_version text NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO content_policy_state(id, revision, evaluator_version)
VALUES (1, 1, 'unicode-confusable-v1')
ON CONFLICT (id) DO NOTHING;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_content_audit_rules_classifier'
    ) THEN
        ALTER TABLE content_audit_rules
            ADD CONSTRAINT ck_content_audit_rules_classifier
            CHECK (classifier IN ('local', 'external'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_content_audit_rules_evaluator_version'
    ) THEN
        ALTER TABLE content_audit_rules
            ADD CONSTRAINT ck_content_audit_rules_evaluator_version
            CHECK (char_length(evaluator_version) BETWEEN 1 AND 64);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_content_audit_logs_classifier'
    ) THEN
        ALTER TABLE content_audit_logs
            ADD CONSTRAINT ck_content_audit_logs_classifier
            CHECK (classifier IN ('local', 'external'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_content_audit_logs_policy_revision
    ON content_audit_logs(policy_revision, created_at);
