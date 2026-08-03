# G0 Risk Register

| Risk | Severity | Detection | Mitigation / exit condition |
| --- | --- | --- | --- |
| Target image omits a migration | P0 | Compare image migration manifest with source tree; empty-db boot | Dockerfile copies every numbered migration; checksum ledger is immutable |
| `002` references absent base tables | P0 | Empty database migration | `002` creates its required base tables before additive constraints |
| Grain state and relational projection diverge | P0 | Reconciliation by aggregate ID/hash | CDC applies through Grain contracts; no Orleans blob CDC |
| Two writers accept the same epoch | P0 | Fence transition audit and write-path counters | Every future write path checks `migration_fence`; only one primary per epoch |
| Target API writes while legacy remains primary | P0 | Fence rejection counter and integration test | `MigrationWriteGate` rejects Platform dispatch, lease settlement/outbox, and business mutations until `target_primary`; `target_canary` is observation-only in G0 |
| Legacy service remains writable after target promotion | P0 | Source-role privilege audit, old-writer counters and promotion drill | G0 does not retrofit Sub2API handlers with the target fence; before any target-primary transition, quiesce the old service and revoke/replace its database write role, then verify read-only behavior |
| Credential leakage to raw topic | P0 | Connector column audit and consumer payload scanner | Password/TOTP/account secret fields are excluded; restricted encrypted channel; rejected raw records retain a digest only |
| Duplicate/late CDC event | P1 | `cdc_inbox` duplicate and lag metrics | event ID primary key, payload hash, grain idempotency and replay tests |
| Consumer exits after inbox claim | P1 | processing age and uncommitted-offset lag | five-minute claim lease is reclaimable after restart; apply remains idempotent |
| WAL slot retention fills legacy disk | P1 | `pg_replication_slots`, WAL bytes and oldest LSN alerts | connector lag SLO, slot cleanup runbook, backpressure before disk pressure |
| Out-of-order aggregate dependencies | P1 | FK/reference and applier failure counts | per-aggregate ordering, retry with backoff, reconciliation before promotion |
| Money precision loss | P0 | Decimal round-trip and ledger reconciliation | CDC envelope preserves decimal strings and target SQL uses NUMERIC(20,8); existing Orleans command/projection DTOs still expose some `double` fields, so G1 must migrate those contracts to decimal or reject unsafe conversion before promotion |
| Cache appears healthy while source is stale | P1 | source-vs-projection version metric | cache is rebuildable and auth version is compared to the Grain projection |
| Fence proof is not yet CI-gated | P1 | F* compiler/prover CI result | `docs/migration/formal/migration_fence.fst` is machine-checked locally with F* 2026.03.24/Z3 4.13.3; add the same command to CI before G1 promotion and keep executable transition tests as the runtime gate |
