-- Run against the legacy Sub2API database as a database administrator with
-- CREATEROLE and publication privileges, after wal_level=logical has been
-- enabled and PostgreSQL has restarted. Production values for the replication
-- role must be supplied out of band; never commit its password.
--
-- psql -v ON_ERROR_STOP=1 -v cdc_user="$CDC_REPLICATION_USER" \
--      -v cdc_database="$POSTGRES_DB" \
--      -v cdc_password="$CDC_REPLICATION_PASSWORD" -f postgres-bootstrap.sql

SELECT set_config('sub2api.cdc_user', :'cdc_user', false) AS configured_cdc_user,
       set_config('sub2api.cdc_password', :'cdc_password', false) AS configured_cdc_password
\gset

DO $$
BEGIN
    IF current_setting('wal_level') <> 'logical' THEN
        RAISE EXCEPTION
            'logical CDC requires wal_level=logical; current value is %',
            current_setting('wal_level');
    END IF;
END
$$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_setting('sub2api.cdc_user')) THEN
        EXECUTE format('CREATE ROLE %I WITH LOGIN REPLICATION PASSWORD %L',
            current_setting('sub2api.cdc_user'), current_setting('sub2api.cdc_password'));
    ELSE
        EXECUTE format('ALTER ROLE %I WITH LOGIN REPLICATION PASSWORD %L',
            current_setting('sub2api.cdc_user'), current_setting('sub2api.cdc_password'));
    END IF;
END
$$;

GRANT CONNECT ON DATABASE :"cdc_database" TO :"cdc_user";
GRANT USAGE ON SCHEMA public TO :"cdc_user";
GRANT SELECT ON TABLE users, groups, accounts, account_groups, user_allowed_groups,
    usage_logs, scheduler_outbox, auth_cache_invalidation_outbox,
    migration_cdc_outbox TO :"cdc_user";

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = 'sub2api_platform_cdc') THEN
        EXECUTE 'CREATE PUBLICATION sub2api_platform_cdc FOR TABLE users, groups, accounts, account_groups, user_allowed_groups, usage_logs, scheduler_outbox, auth_cache_invalidation_outbox, migration_cdc_outbox';
    END IF;
END
$$;
