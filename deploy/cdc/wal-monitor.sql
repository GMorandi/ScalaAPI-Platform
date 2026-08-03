-- Run on the legacy source database at a fixed interval (or export the result
-- to Prometheus). Alert when retained WAL grows or the slot is inactive.
SELECT
    slot_name,
    plugin,
    slot_type,
    active,
    restart_lsn,
    confirmed_flush_lsn,
    pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)) AS retained_wal,
    pg_wal_lsn_diff(pg_current_wal_lsn(), confirmed_flush_lsn) AS pending_bytes
FROM pg_replication_slots
WHERE slot_name = 'sub2api_platform_cdc';
