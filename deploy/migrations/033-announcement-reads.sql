-- User-owned announcement read state. One user can acknowledge an announcement once.
CREATE TABLE IF NOT EXISTS announcement_reads (
    user_id bigint NOT NULL REFERENCES user_accounts(id) ON DELETE CASCADE,
    announcement_id bigint NOT NULL REFERENCES announcements(id) ON DELETE CASCADE,
    read_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, announcement_id)
);
CREATE INDEX IF NOT EXISTS ix_announcement_reads_user_read
    ON announcement_reads(user_id, read_at DESC);
