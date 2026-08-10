-- Make the OpenAI Moderation adapter selectable by the new policy contract.
-- Existing local and source-owned external rules remain valid; this is a
-- forward-only schema change for the independent ScalaAPI product.
ALTER TABLE content_audit_rules
    DROP CONSTRAINT IF EXISTS ck_content_audit_rules_classifier;
ALTER TABLE content_audit_rules
    ADD CONSTRAINT ck_content_audit_rules_classifier
    CHECK (classifier IN ('local', 'external', 'openai'));

ALTER TABLE content_audit_logs
    DROP CONSTRAINT IF EXISTS ck_content_audit_logs_classifier;
ALTER TABLE content_audit_logs
    ADD CONSTRAINT ck_content_audit_logs_classifier
    CHECK (classifier IN ('local', 'external', 'openai'));
