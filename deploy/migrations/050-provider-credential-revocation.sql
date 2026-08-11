-- A confirmed OAuth invalid_grant is a terminal credential lifecycle outcome.
-- The audit row remains metadata-only and records the monotonic generation change.
ALTER TABLE provider_credential_refresh_attempts
    DROP CONSTRAINT IF EXISTS provider_credential_refresh_attempts_outcome_check;
ALTER TABLE provider_credential_refresh_attempts
    DROP CONSTRAINT IF EXISTS provider_credential_refresh_attempts_check;

ALTER TABLE provider_credential_refresh_attempts
    ADD CONSTRAINT ck_provider_credential_refresh_outcome
        CHECK (outcome IN ('succeeded', 'failed', 'revoked')),
    ADD CONSTRAINT ck_provider_credential_refresh_result
        CHECK ((outcome = 'succeeded'
                AND version_after = version_before + 1
                AND error_code IS NULL)
            OR (outcome = 'failed'
                AND version_after IS NULL
                AND error_code IS NOT NULL
                AND length(error_code) BETWEEN 1 AND 120)
            OR (outcome = 'revoked'
                AND version_after = version_before + 1
                AND error_code IS NOT NULL
                AND length(error_code) BETWEEN 1 AND 120));
