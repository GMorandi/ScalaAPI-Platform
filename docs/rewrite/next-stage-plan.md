# ScalaAPI Next Stage Plan

## Checkpoint

The next stage starts from Platform `63befca`, Gateway `dc69269`, and read-only
reference `sub2api@43ec48d`.

The greenfield baseline now starts from empty volumes, uses PostgreSQL as authority,
Garnet as the only distributed projection/cache, S3-compatible object storage for
media, and a source-owned Provider mock. The checked-in gate proves idempotent
migrations, Chat settlement/replay, clean requests after Platform/Gateway
replacement, and no-charge outcomes for non-stream OpenAI Chat 429, 500, malformed
usage, upstream disconnect, and timeout. New users start at zero; administrative
funding is now a required-key PostgreSQL ledger command with exact replay, conflict,
actor audit, active-hold overdraft protection, and retryable Orleans projection.

This stage contains no compatibility, cutover, dual-write, CDC, snapshot import,
old-key import, ID preservation, status mapping, or business-data migration work.

## Objective

Close one production-shaped, billable OpenAI Chat vertical slice under cancellation,
partial output, process crashes, cache loss, and multi-instance operation:

```text
new user/session/key/group/account
  -> Gateway JSON or SSE
  -> Platform schedule/lease/hold
  -> Provider adapter/mock
  -> usage report and idempotent settlement
  -> PostgreSQL ledger/reconciliation
  -> Admin query and operational evidence
```

The slice remains `partial` until every exit scenario below is automated and a
failed assertion makes the top-level command non-zero.

## Work package 1: accounting authority and recovery

Completed foundation at `63befca`:

- Removed balance from user create/configuration API and Admin Web contracts.
- Added unique `admin_adjustment` effects with reason/actor audit, exact replay,
  conflict detection, per-user SQL serialization, active-hold protection, and an
  authoritative projection snapshot.
- Proved the contract in a real-database test and in the empty-volume Compose gate.

Next implementation slice:

- Add one per-user accounting account row with a monotonically increasing ledger
  version and authoritative NUMERIC posted balance. Every ledger write returns the
  resulting version and balance.
- Route payment credit/refund, redeem bonus, subscription grant, administrative
  adjustment, and usage debit through one accounting repository and one per-user
  serialization rule. Remove unordered Orleans delta application.
- Persist the last applied ledger version in the user Grain. Ignore stale snapshots,
  apply newer snapshots atomically, and allow reconciliation to repair a missing
  projection without replaying money.
- Make durable hold reservation and debit availability checks use the same account
  authority so concurrent usage and administrative changes cannot publish a stale
  balance.
- Add a reconciliation worker that compares account balance/version, active holds,
  ledger effects, and Grain projection, repairs safe projection drift, and persists
  operator-visible failures.

Remaining package deliverables:

- Define one documented lease state machine and legal transitions for held,
  forwarded, output-started, completed, aborted, expired, and reconciliation-needed
  outcomes. Terminal transitions must be idempotent.
- Move every remaining balance mutation behind unique, append-only PostgreSQL ledger
  effects. Orleans may project a committed version but must not be the only authority
  for money.
- Add a reconciliation worker for expired leases, active holds, committed usage,
  and idempotency rows. It must distinguish safe release from an unknown Provider
  charge that needs operator reconciliation.
- Add deterministic fault hooks immediately before/after Provider dispatch, after
  Provider completion, before/after the settlement SQL commit, and before outbox
  acknowledgement.
- Prove duplicate completion, abort, expiry, worker reclaim, and outbox replay never
  create a second debit or lose a committed debit.

Dependencies: migration 017 administrative effects, existing lease snapshots,
durable holds, response replay, settlement outbox, and reconciliation query.

Exit: every injected crash converges after restart to zero orphan active holds, one
terminal lease, at most one usage debit, and a durable operator-visible reason when
the Provider charge is unknowable.

## Work package 2: cancellation and streaming failure semantics

Deliverables:

- Propagate client cancellation through the HTTP/SSE transport and stop retrying as
  soon as any response bytes have reached the client.
- Before Provider output starts, cancellation must abort the lease, release the hold,
  and produce no usage/debit.
- After Provider output starts, do not assume zero cost. Continue collecting a final
  usage record when possible; otherwise terminate as reconciliation-needed with the
  hold and operator state defined by package 1.
- Extend the isolated fault accounts to SSE: 429, 500, timeout before first event,
  disconnect before first event, disconnect after partial output, malformed usage,
  invalid content type, and client disconnect.
- Add bounded-buffer/backpressure assertions and verify that partial output cannot
  be replayed as a complete response or retried against another account.

Dependencies: package 1 terminal/reconciliation states.

Exit: JSON and SSE fault matrices specify response status, retry count, output-start
state, terminal lease, hold, usage, debit, idempotency, and reconciliation outcome.

## Work package 3: protocol contract fixtures

Deliverables:

- Version golden fixtures for OpenAI Chat request/response, tools, streaming events,
  usage, finish reasons, status codes, headers, and safe error bodies.
- Cover same-protocol and cross-protocol normalization at the Gateway boundary.
- Validate request IDs, idempotency fingerprints, Provider status mapping, proxy/TLS
  headers, response limits, and malformed payload rejection.
- Keep the revision-1 Cap'n Proto schema greenfield. Contract changes update the
  canonical Platform source, generated C#, Gateway vendor copy, and both digest
  gates as one coordinated release change; no deprecated compatibility fields are
  added.

Dependencies: package 2 defines terminal streaming behavior.

Exit: fixtures are deterministic, run without external Providers, and cover every
Chat success/failure branch used by the deployment gate.

## Work package 4: Garnet and cluster resilience

Deliverables:

- Run at least two Gateway instances and two Orleans Silos against one authenticated
  Garnet service and PostgreSQL authority.
- Add Garnet TLS 1.2/1.3 tests with certificate-name validation, concurrent clients,
  password rejection, flush, stale invalidation version, restart, and projection
  rebuild.
- Prove cache loss fails new rate-sensitive dispatch closed but does not block usage
  settlement, hold recovery, or outbox drain.
- Exercise Silo removal, Gateway rolling replacement, and concurrent requests for
  the same API key/idempotency key without duplicate account leases or charges.
- Keep all cache keys under the documented `scalaapi:v1` namespace with owner,
  schema, TTL, and rebuild source recorded.

Dependencies: package 1 PostgreSQL authority and recovery.

Exit: multi-instance flush/outage/restart tests pass with zero cache-as-authority
behavior and no Redis process, package, image, CLI, or embedded fallback.

## Work package 5: blocking release workflow and operability

Deliverables:

- Give hosted CI a read-only sibling-repository checkout credential or move the
  cross-repository gate into a dedicated release repository. Run the exact
  `deploy/stack/smoke.sh` entry point from empty volumes.
- Record both commit IDs, source-built image IDs/digests, migration checksums,
  environment shape, scenario names, and top-level exit code.
- Emit structured correlation for client request, internal retry/lease,
  idempotency, account, Provider request, usage, and ledger effect IDs without
  logging secrets or raw API keys.
- Add metrics and alerts for active/expired holds, reconciliation-needed leases,
  settlement retry age, Gateway outbox backlog, Garnet readiness, Provider fault
  rate, and ledger mismatch.
- Retain benchmark integrity checks: zero selected benchmarks or any failed child
  process must return non-zero. Performance claims require a separate measured run.

Dependencies: packages 1-4 expose the states and scenarios to observe.

Exit: one hosted, blocking workflow recreates the local evidence and intentionally
failing probes demonstrate that migration, fixture, benchmark, and fault-scenario
failures all fail CI.

## Required acceptance matrix

Each scenario records client result, retries/accounts used, output-start state,
lease transitions, hold state, usage events/logs, request log, ledger effects,
idempotency state, outbox backlog, and reconciliation status:

1. JSON success and settled exact replay.
2. SSE success with complete usage.
3. Same key while active and same key with a conflicting fingerprint.
4. Provider 429 and 500 exhaustion.
5. Malformed usage and malformed/truncated successful JSON.
6. Timeout and upstream disconnect before output.
7. Upstream disconnect after partial SSE output.
8. Client disconnect before and after Provider output.
9. Gateway crash at dispatch and usage-report boundaries.
10. Platform crash around settlement commit and outbox acknowledgement.
11. Garnet flush, outage, TLS failure, and rebuild with concurrent Gateways.
12. Silo removal and rolling Gateway/Platform replacement.

## Sequence and commit discipline

1. Finish package 1 account versioning and projection repair first because
   cancellation and cluster behavior need an authoritative unknown-charge and
   reconciliation state.
2. Package 2 defines transport semantics; package 3 freezes them as fixtures.
3. Package 4 runs the state machines under concurrency and infrastructure failure.
4. Package 5 makes the same evidence mandatory in hosted release CI.

Implement each independently verifiable functional point in its owning repository,
with focused tests and a detailed commit message describing contract, failure
semantics, and evidence. Update `current-state.md`, `verification.md`, the affected
inventory acceptance row, and this checkpoint after each completed package.

## Stage exit and following expansion

The stage exits only when all 12 scenarios pass from an empty environment locally
and in hosted CI, all monetary invariants reconcile, and OpenAI Chat can be promoted
using the inventory's contract/test/runtime rule.

Then expand the remaining 58-domain work in this order:

1. Complete OpenAI Responses/Embeddings/Images/video/realtime, Anthropic Messages,
   Gemini generation, model catalogue/token counting, and cross-protocol fixtures.
2. Complete Provider OAuth/credential refresh, price/quota adapters, media recovery,
   and object reconciliation/restore.
3. Complete identity hardening, TOTP, Passkeys, OAuth binding, User Web, API-key,
   usage, order, subscription, and recovery workflows with browser tests.
4. Complete payment adapters/reconciliation/refunds, subscription workers, redeem,
   affiliate, notification, and commercial audit flows.
5. Complete policy/security, observability, multi-region/HA, load and long-connection
   soak, backup/restore, signed updates, and rollback drills.

Every later domain uses new ScalaAPI contracts and clean seed data. Sub2API remains
read-only research material and is never an acceptance oracle for compatibility.
