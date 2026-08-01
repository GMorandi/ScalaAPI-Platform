-- Drop existing objects
DROP TABLE IF EXISTS OrleansMembershipTable CASCADE;
DROP TABLE IF EXISTS OrleansMembershipVersionTable CASCADE;
DROP TABLE IF EXISTS OrleansStorage CASCADE;
DROP TABLE IF EXISTS OrleansRemindersTable CASCADE;
DROP TABLE IF EXISTS OrleansQuery CASCADE;
DROP FUNCTION IF EXISTS update_i_am_alive_time CASCADE;
DROP FUNCTION IF EXISTS insert_membership_version CASCADE;
DROP FUNCTION IF EXISTS insert_membership CASCADE;
DROP FUNCTION IF EXISTS update_membership CASCADE;
DROP FUNCTION IF EXISTS writetostorage CASCADE;
DROP FUNCTION IF EXISTS upsert_reminder_row CASCADE;
DROP FUNCTION IF EXISTS delete_reminder_row CASCADE;

-- OrleansQuery lookup table
CREATE TABLE OrleansQuery
(
    QueryKey varchar(64) NOT NULL,
    QueryText varchar(8000) NOT NULL,
    CONSTRAINT OrleansQuery_Key PRIMARY KEY(QueryKey)
);
