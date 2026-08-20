-- Add verification columns to backup_offsite_uploads for remote readback proof.
ALTER TABLE backup_offsite_uploads
    ADD COLUMN IF NOT EXISTS verified_at timestamptz,
    ADD COLUMN IF NOT EXISTS verify_status text CHECK (verify_status IS NULL OR verify_status IN ('verified', 'mismatch', 'unreachable'));
