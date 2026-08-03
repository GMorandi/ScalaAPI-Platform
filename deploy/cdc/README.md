# Local CDC bootstrap

The root compose file enables PostgreSQL logical WAL settings. Start the CDC
overlay only for local validation:

```sh
docker compose -f docker-compose.yml -f platform/deploy/cdc/docker-compose.yml --profile cdc up -d postgres redpanda debezium
```

On the legacy Sub2API database, have a database administrator with `CREATEROLE`
and publication privileges create the dedicated replication user and
publication with `postgres-bootstrap.sql`. Supply the two `psql -v` values from
an untracked environment file. The publication is deliberately explicit so a
new table cannot silently enter the migration stream.

Apply Sub2API migration `192_migration_cdc_outbox.sql` before creating the
publication. Immediately before the initial Debezium snapshot, run
`SELECT emit_api_key_migration_cdc_snapshot();` exactly once and record its row
count. The semantic outbox contains the Gateway-compatible SHA-256 identity;
the connector never reads the raw `api_keys` table.

Poll `wal-monitor.sql` from the legacy database and alert on an inactive slot,
growing `retained_wal`, or `pending_bytes` over the migration SLO. The slot is
never dropped automatically when the connector stops.

Register `debezium-connector.json` through the Connect REST API after replacing
the `${...}` placeholders from the runtime environment. Debezium's raw records
are accepted by the Platform consumer and normalized to `ChangeEnvelope v1`
before inbox insertion. The consumer remains disabled by default until the
initial snapshot and LSN checkpoint have been audited.

The connector excludes user authentication secrets, account credentials and
`extra`, scheduler payload JSON, operational error strings, and usage client
identifiers. The consumer independently rejects known credential field names;
`cdc_rejected_messages` records only the digest and broker position.

Debezium 3.x PostgreSQL records may encode `source.lsn` as a decimal number and
use `source.snapshot` values such as `first`, `last_in_data_collection`, and
`last`. The adapter preserves these markers, parses both numeric and `X/Y` LSN
forms, and only marks the target snapshot complete on the final `last` marker.
JSONB values emitted by the JSON converter may be strings; semantic outbox
payloads are parsed back into objects before validation.

Platform business writes are fenced independently of CDC consumption. With the
initial `legacy_primary` fence, dispatch, lease settlement/outbox and Admin
business mutations return a migration-fence rejection instead of creating a
second writer. `target_canary` is observation-only in G0; ordinary target
business writes are enabled only at `target_primary`. Promotion must therefore
be an explicit, audited fence change, and the target-primary transition also
requires a completed snapshot checkpoint with no outstanding Inbox/dead-letter
work.

Credential synchronization uses a separate `CredentialEnvelope v1` topic and
the `cdc_credential_payloads` table. No account credential producer is enabled
by the G0 compose overlay; G1 must provision the topic ACL, target key version,
decrypt-and-hash verification, and the account Grain apply handler before any
credential-bearing account can be selected by Platform.

The target-side inbox recovery integration test is opt-in:
`CDC_TEST_CONNECTION='Host=...;Database=sub2api;Username=...;Password=...'`
`dotnet test test/Host.Tests/Host.Tests.csproj`. It exercises duplicate
delivery, payload-hash collision, stale processing-lease reclaim, the 25-attempt
dead-letter transition, and replay. Without the variable, the normal contract
test suite remains database-free.

For an explicit target schema parity check, set
`CDC_SCHEMA_CONNECTION` and run the same test project. The check covers the
SQLSugar projection entities, leases/settlement tables, and every CDC control
table, including the monotonic LSN checkpoint columns.

The isolated broker proof is opt-in and does not touch the running application:
set `CDC_BROKER_BOOTSTRAP`, `CDC_SOURCE_CONNECTION`, and optionally
`CDC_BROKER_TOPIC`, then run `dotnet test --filter CdcBrokerE2ETests`. It waits
for the real Debezium snapshot-final marker, normalizes the semantic API-key
outbox, mutates the temporary source, and requires a strictly larger
post-snapshot LSN. Heartbeats are ignored; malformed data records still go
through the normal adapter validation path.
