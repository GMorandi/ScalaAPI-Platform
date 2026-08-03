# G0 Migration Contract

## Authority and epochs

`migration_fence` is the only durable write-authority record. The initial row is
`epoch=1`, `write_primary=sub2api`, `mode=legacy_primary`. A transition locks the
row, validates the expected current primary, increments the epoch, and commits a
single new primary. Each transition is also appended to
`migration_fence_events` with the old/new epoch, modes, reason and operator;
the history endpoint is retained for the audit window. No service is allowed to
infer authority from process state.

Promotion to `target_primary` is rejected unless at least one connector has a
completed snapshot checkpoint and the target has no pending/failed/processing
Inbox rows or unreplayed dead letters. This is a minimum safety gate, not a
replacement for reconciliation or the source-side read-only procedure.

## Cutover performance boundary

The cutover critical path must not copy or lock the business tables. Snapshot
hydration and WAL/CDC apply run before cutover; the final operation is: quiesce
the legacy writer, verify the source LSN/target checkpoint and queue drain, then
commit one row-locked `migration_fence` transition. That makes cutover work
constant-size with respect to database volume. CDC lag and reconciliation time
remain part of the preparation window, not the fence transaction.

The fence therefore provides a linearizable authority decision, not a
distributed transaction for every business write. F* can machine-check the
finite-state safety rules, while throughput and latency must be measured with
CDC lag, WAL retention, lock-wait, and cutover-drill benchmarks.

The target accepts older in-flight events (`event.epoch <= fence.epoch`) but
rejects future epochs. Promotion therefore drains or explicitly accounts for
events from the previous epoch before accepting new target writes.

## ChangeEnvelope v1

```json
{
  "event_id": "uuid",
  "epoch": 1,
  "source_lsn": "0/16B6C50",
  "transaction_id": "734",
  "aggregate_type": "user|group|api_key|account|usage",
  "aggregate_id": "123",
  "operation": "insert|update|delete|snapshot",
  "schema_version": 1,
  "occurred_at": "2026-08-03T00:00:00Z",
  "payload_hash": "sha256-hex",
  "payload": {}
}
```

`event_id` is the idempotency key. `source_lsn` and `transaction_id` provide
ordering and traceability; `payload_hash` is checked before the inbox write.
Debezium records are normalized to this shape at the consumer boundary. A
payload must contain no plaintext credential or token.

## SyncAck v1

The target records `accepted`, `applied`, `rejected`, or `failed` in
`cdc_sync_acks`. For synchronous control domains, the source write path waits
for `applied` for the same event/aggregate before returning success. This is a
durable application acknowledgement, not a distributed transaction. G0 only
persists and exposes the acknowledgement; ordinary Debezium table changes are
observable after the source transaction commits, so the source request path
cannot yet wait on a correlation it did not create. G1 must add a transactional
source outbox/correlation (or an explicitly synchronous control-domain write
protocol) before claiming synchronous user/account/group/balance writes.

## Restricted credentials

Account credential changes are excluded from ordinary CDC. They use the
ACL-protected `cdc_credential_payloads` channel, with an envelope encrypted for
the target key version and a separately verified payload hash. Ordinary account
events contain metadata only; if a raw event contains a credential field, the
consumer dead-letters it rather than clearing or fabricating credentials. API
key events use the source `migration_cdc_outbox`, which contains only the
Gateway-compatible SHA-256 hash and never the plaintext key.

`CredentialEnvelope v1` is persisted as ciphertext bytes plus key version and
hash, together with source LSN, transaction, operation, and occurred-at
metadata. Its store rejects plaintext/non-`enc:v1` values and identity hash
collisions, and is separate from `cdc_inbox`; a future credential consumer must
decrypt, verify the hash, apply through `IAccountGrain`, then mark `applied_at`.

## Invariants

1. At most one value is permitted for `migration_fence.write_primary`.
2. Redis/Garnet is a rebuildable projection, never an event source.
3. `cdc_inbox.event_id` and grain lease/finalization keys make replays no-ops.
4. Failed events remain queryable, retry with backoff, and become explicit dead
   letters after 25 attempts; a five-minute processing lease makes an interrupted
   claim reclaimable. Replay resets the inbox row without deleting the original
   evidence.
5. Orleans storage blobs are never decoded or replicated by CDC.
6. Reusing an `event_id` with a different payload hash is a rejected identity
   collision, never an idempotent replay. Invalid records retain only their
   broker position, byte count and SHA-256 digest, not their potentially secret
   raw payload.
7. Platform dispatch, lease settlement/outbox writes, and Admin business
   mutations pass through `MigrationWriteGate`; they are rejected while the
   fence is `sub2api/legacy_primary` or observation-only `platform/target_canary`.
   Migration control and read/login paths are separate and do not silently
   become a second writer.

The fence is not a magic cross-process transaction. G0 wires the target-side
authority gate; the legacy Go service is not yet fence-aware. A target-primary
promotion therefore also requires an operational source quiesce and database
role change to make Sub2API read-only. `PromoteAsync` alone must never be treated
as permission to switch production writes.

`target_canary` is observation-only in G0. The ordinary Platform write gate
opens only at `target_primary`; a future scoped canary writer must fence the
corresponding source tenants before it is enabled.

G0 deliberately does not claim end-to-end monetary precision: the envelope and
SQL projection preserve decimal strings/`NUMERIC(20,8)`, while several existing
Orleans command DTOs still use `double`. That contract change is a G1 gate before
financial cutover.
