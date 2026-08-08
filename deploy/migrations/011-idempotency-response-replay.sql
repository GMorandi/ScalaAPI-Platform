-- Completed non-streaming requests retain a bounded response so an identical
-- retry can be served without allocating a second lease or applying billing.
ALTER TABLE request_idempotency
    ADD COLUMN IF NOT EXISTS response_status_code integer;
ALTER TABLE request_idempotency
    ADD COLUMN IF NOT EXISTS response_content_type text;
ALTER TABLE request_idempotency
    ADD COLUMN IF NOT EXISTS response_body text;
ALTER TABLE request_idempotency
    ADD COLUMN IF NOT EXISTS completed_at timestamptz;

ALTER TABLE request_idempotency
    ADD CONSTRAINT ck_request_idempotency_response_body_size
    CHECK (response_body IS NULL OR octet_length(response_body) <= 4194304);
