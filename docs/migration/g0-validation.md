# G0 Validation Evidence

Validated on 2026-08-03 without changing Gateway routes, restarting the running
application stack, or connecting CDC to the live Sub2API database.

## Target schema

- A fresh PostgreSQL 17 database applied migrations `000` through `007`.
- A second migrator run skipped all eight files with matching SHA-256 checksums.
- `MigrationSchemaTests` verified the SQLSugar entities, lease/settlement schema,
  and CDC control tables. Migration `007` closes the remaining Redeem entity
  column gap and adds numeric/partition broker checkpoint fields.
- The current target database also contains `000` through `007`; its fence
  remains `epoch=1/sub2api/legacy_primary`, with empty CDC evidence tables.

## Formal fence proof

- F* 2026.03.24 with Z3 4.13.3 discharged every verification condition in
  `formal/migration_fence.fst`.
- The proof covers legal transition edges, mode/primary consistency, exact epoch
  increment, target-primary readiness, and the target write-enable condition.
- `formal/verify.sh` is the reproducible CI entry point. The proof does not make
  a throughput or database-lock latency claim.

## Isolated CDC end to end

The isolated stack used PostgreSQL 16 with logical WAL, Redpanda 24.3.6, and
Debezium PostgreSQL Connector 3.0.8.Final (Kafka Connect runtime 3.9.0).

- The explicit publication and a `pgoutput` replication slot were active.
- Debezium completed an initial snapshot across users, groups, accounts,
  relations, scheduler/auth outboxes, and the semantic API-key outbox.
- A post-snapshot user update advanced the source LSN beyond the snapshot LSN.
- The latest Platform silo consumed and applied 10 events through the Grain
  applier: two user snapshots, one user update, and one snapshot each for group,
  account, account-group, allowed-group, API key, scheduler invalidation, and
  auth-cache invalidation.
- Target result: 10 Inbox rows, 10 `applied` ACKs, zero failed/dead-letter rows,
  zero rejected records, and a completed checkpoint at decimal LSN `27074520`.
- A new consumer group replayed the topic from offset zero; Inbox and ACK counts
  remained 10, proving the broker replay was idempotent.
- Debezium heartbeats were committed as non-business records and did not pollute
  `cdc_rejected_messages`.

## Regression gates

- `Host.Tests`: 31 passed, including fresh-database schema, promotion/rollback,
  credential ciphertext, inbox lease/dead-letter/replay, checkpoint monotonicity,
  and broker E2E tests when their opt-in environment variables are supplied.
- `Grains.Tests`: 50 passed.
- `Admin.Api`: build succeeded with zero warnings and zero errors.
- Platform migrate, silo, and Admin images built successfully; the migrate image
  contains migrations `000` through `007`.

## Deliberate G1 boundaries

- The live Sub2API database was not modified; its source migration, role,
  publication, and snapshot must be applied in a controlled G1 window.
- Ordinary post-commit Debezium cannot provide request-level synchronous ACK;
  G1 needs a source transactional outbox/correlation protocol.
- The restricted account-credential producer/consumer and topic ACL remain G1.
- Existing Orleans money DTOs still contain `double`; decimal contract migration
  and ledger reconciliation remain a pre-promotion G1 gate.
- The running target PostgreSQL instance still reports `wal_level=replica`; the
  compose definition is ready for `logical`, but no restart was performed.
