# ScalaAPI Next Stage Plan

## Checkpoint

The next stage starts from Platform `90597b8`, Gateway `eb5734f`, and read-only
reference `sub2api@43ec48d`.

The greenfield baseline now starts from empty volumes, uses PostgreSQL as authority,
Garnet as the only distributed projection/cache, S3-compatible object storage for
media, and a source-owned Provider mock. The checked-in gate proves idempotent
migrations, Chat settlement/replay, clean requests after Platform/Gateway
replacement, and evidence-based failure outcomes for non-stream and selected
streaming OpenAI Chat. An
actual non-stream and streaming 429/500 Provider rejections release; malformed
success, upstream disconnect, timeout, and invalid streaming media type make no
second attempt and retain their holds for reconciliation.
PostgreSQL now owns one ordered account per
user; administrative, payment/refund, redeem, and usage effects share one append
rule, SQL holds authorize dispatch, and Orleans is a retryable versioned projection.
Leases persist immutable `held`, `forwarded`, and `output_started` evidence. Only a
never-forwarded held lease may expire and release; all forwarded ambiguity becomes
`reconciliation_needed`, with its hold and idempotency key reserved until a late
completion or future operator decision. A globally serialized scheduled
reconciler checks the full account/ledger/usage/hold/projection boundary, performs
only provably safe repairs, persists incidents, and exposes Admin queries and
metrics. An Admin-only, token-protected operator command now settles or releases
one open unknown-charge incident exactly once with actor, evidence, reason, lease
event, and audit persistence in the same transaction; subsequent reconciliation
preserves that decision. Gateway and Platform now expose deterministic one-shot
fault hooks around dispatch, Provider completion, settlement commit, outbox claim,
and outbox acknowledgement. The source smoke also exercises the outbox-claim boundary and
intentionally crashed Platform before settlement
commit, after settlement commit, and before outbox acknowledgement, explicitly
restarted the same container, and recovered a single Orleans silo without a
duplicate debit; Gateway reconnect/backoff recovery drained the durable usage
outbox. Gateway now
has source-level terminal-event-gated SSE completion, incomplete chunked-body
classification, and client-cancellation classification. The empty stack proves
Provider disconnect, disconnect-before-output, malformed-usage, timeout before
response headers, and actual downstream client-cancellation and invalid-content-type
SSE retention with nine total unknown-charge incidents. The pre-header timeout now
returns a bounded 502/provider_protocol_error and retains its hold; direct and
zero-output Provider resets now return 503/provider_unavailable, while partial SSE
resets retain their unknown-charge hold. Gateway CTest now independently proves the
inter-chunk and total-stream timers. The `disconnect_after_usage` profile emits valid
usage and ends before `[DONE]`; the empty-stack gate settles it exactly once through
the durable outbox. The latest source smoke also crashes Gateway after Provider
completion, restarts the same container, and retains the forwarded lease/hold as
one reconciliation incident without a debit. Gateway before Provider dispatch and
Platform before Provider dispatch now safely expire their unforwarded held leases
after the same container is restarted. A Platform worker crash immediately after
claiming a completed outbox event is also reclaimed and applied once. Platform
dispatch responses now expose a dedicated retryable `platformUnavailable` code;
Gateway retries it with bounded backoff under the existing deadline, and Platform
rebuilds the original active lease target after process loss. The Chat smoke proves
one lease, usage event, usage log, and debit after this recovery. Realtime/other
Gateway retry paths, remaining boundaries, the worker/multi-silo matrix, and
multi-instance scenarios are still open.

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

## Work package 1: reconciliation and exact-boundary recovery

Accounting authority completed at `c15b53b`, reconciliation foundation at
`fddba62`, dispatch evidence at `6bfb974`/`84634d1`, audited resolution at
`0559659`, and deterministic fault boundaries at `1cad5b7`/`30b8c2b`/`8c3d2e0`,
with current streaming/empty-stack evidence in Gateway `eb5734f` and Platform
`90597b8`:

- Added one per-user `accounting_accounts` authority with NUMERIC posted balance
  and monotonically increasing ledger version.
- Routed administrative adjustments, payment credits/refunds, redeem bonuses, and
  usage debits through one per-user SQL serialization and stable effect contract.
- Moved hold reservation, availability checks, completion, abort, and TTL handling
  into the SQL authority; Grain no longer owns money or permits dispatch.
- Added versioned Grain snapshots, latest-only projection outbox, retry worker, and
  backlog/retry metrics. Stale snapshots cannot regress a newer balance.
- Proved 20 concurrent versions, replay/conflict, hold oversubscription, protected
  debit, account/ledger equality, projection drain, migration idempotency, service
  replacement, and isolated Provider fault accounting.
- Replaced unsafe ambiguous TTL release with `reconciliation_needed`, an active
  hold, blocked matching redispatch, a reconciliation outbox event, and exactly-once
  late usage completion; dispatch evidence now narrows safe expiry to `held` only.
- Added globally serialized scheduled/manual reconciliation of account balance and
  version, ledger contiguity, usage/debit equality, lease/hold state, and Grain
  projection. Safe terminal-hold and stale-projection drift is repaired; every
  unknown charge or unsafe mismatch is a durable incident.
- Added protected run/incident APIs and metrics for open count, unknown-charge count,
  oldest age, and last successful run. Real PostgreSQL tests prove repair,
  persistence, late settlement, and later incident resolution.
- Added the strict `held -> forwarded -> output_started -> completed` state machine,
  terminal `aborted`/`expired`/`reconciliation_needed` branches, and an immutable
  transition-event journal. Evidence writes and terminal writes are idempotent.
- Gateway persists `forwarded` before HTTP/realtime transport and reports the first
  successful streaming client write. Failure to persist forwarding evidence fails
  closed before Provider contact.
- Restricted no-charge release and failover to actual Provider 4xx/5xx responses.
  Transport loss, synthesized errors, malformed success, conversion failure, and
  media persistence ambiguity retain the hold and do not fail over.
- Proved migration 020 idempotency, safe never-forwarded expiry, retained unknown
  aborts, late exactly-once settlement, and a source-built fault matrix with three
  intentional unknown-charge incidents.
- Added migration 021 and a native resolution contract. `settle` validates bounded
  usage/evidence and calls the same completion transaction as normal Provider
  usage; `release` accepts only `never_forwarded`, `provider_rejection`, or
  `provider_confirmed_no_charge` evidence, checks the lease journal, releases the
  hold, and records no usage. Both actions lock incident/lease/account state,
  persist a resolution row, operator lease event, and actor audit, and use a global
  idempotency key plus request fingerprint for replay/conflict behavior.
- Added an Admin API endpoint and token-protected Platform internal bridge. A real
  PostgreSQL Host test covers settle/release atomicity, one debit/hold transition,
  invalid evidence, same-key conflict, and concurrent different-key serialization;
  source smoke settles one incident, replays it as `duplicate`, and verifies the
  next reconciliation preserves the decision.

Implemented in this package:

- Added Gateway and Platform one-shot, marker-backed fault hooks before/after
  Provider dispatch, after Provider completion, before/after settlement commit,
  after outbox claim, and before outbox acknowledgement. Unit tests prove exact
  hook matching, one-shot claims, and repeat mode; the empty-stack recovery harness now waits
  for post-commit, after-claim, and pre-ack process termination, restarts the same container,
  and proves the durable outbox produces one terminal debit. The
  `scalaapi-gateway-recovery-0907` source smoke additionally terminates Gateway
  after Provider completion, explicitly starts the same container, and proves the
  original lease remains reconcilable with no usage/debit or repeat crash.
- The `scalaapi-gateway-dispatch-recovery-0911` source smoke terminates Gateway
  before Provider dispatch, explicitly starts the same container, and proves
  safe `held -> expired` cleanup with released hold/idempotency and no incident.
- The `scalaapi-platform-dispatch-recovery-0912` source smoke terminates Platform
  after creating the SQL lease/hold but before returning the dispatch target,
  explicitly starts the same container, and proves the same safe `held -> expired`
  cleanup with released hold/idempotency and no incident. Its failure probe uses
  `curl -f` so a Gateway-wrapped HTTP error cannot be mistaken for success.
- The `scalaapi-platform-worker-recovery-0913` source smoke terminates Platform
  after claiming the completed settlement outbox but before any Grain side effect,
  explicitly starts the same container, and proves the expired claim is reclaimed
  and applied once with no duplicate financial effect.
- The `scalaapi-platform-dispatch-retry-0914` source smoke terminates Platform
  after the lease/hold commit. Gateway retries the same request and the replacement
  Platform rebuilds the active lease target; the request settles one lease, usage
  event, usage log, and NUMERIC debit. The full matrix passes. The smoke uses a
  temporary runtime image assembled from the verified local Gateway build because
  the pinned Photon commit is unavailable for a clean image build.
- Added explicit `Orleans:SingleSiloRecovery` for the development smoke path and
  a Podman-compatible harness restart. The source smoke proved
  `platform.before_settlement_commit`, `platform.after_settlement_commit`, and
  `platform.before_outbox_ack` crashes, durable usage replay, and exactly one
  usage debit; the Podman harness starts an exited container explicitly before
  waiting for settlement.

Next implementation slice:

- Exercise every remaining hook independently with replay assertions for duplicate
  completion, abort, expiry, projection replacement, and process restart. Platform
  dispatch retry and active-lease recovery are proven for regular Chat; next cover
  realtime and other Gateway dispatch paths, remaining Gateway hooks, and multi-silo
  recovery before promoting the billing slice.

Remaining package deliverables:

- Define authority contracts before adding subscription grants, affiliate rebates,
  or any new monetary effect. They must use the same account/version API and cannot
  write `balance_ledger` directly.
- Add a blocking negative probe for each new fault hook so a swallowed child failure or
  missing scenario makes the top-level gate non-zero.

Dependencies: migrations 018-021 accounting authority/reconciliation/evidence,
versioned ledger effects, durable holds, response replay, settlement/projection
outboxes, persisted incident identity, and the audited resolution transaction.

Exit: every injected crash converges after restart to one terminal lease or one
documented `reconciliation_needed` lease, at most one usage debit, no unaccounted
hold, and a durable operator-visible reason when the Provider charge is unknowable;
an open incident can be resolved only through the audited settle/release contract.

## Work package 2: cancellation and streaming failure semantics

Progress in Gateway `eb5734f` and Platform `90597b8`: the streaming pipe now requires a source protocol
terminal event before treating Provider EOF as complete, classifies timeout/EOF as
incomplete (including Photon incomplete chunked-body `-1/errno=0`), treats
zero/error client writes as cancellation, records bounded
disconnect/cancellation reasons, and prevents Gateway failover or normal usage
settlement for ambiguous partial streams. These behaviors are covered by 102 Gateway
CTest cases. Platform smoke proves Provider disconnect, disconnect-before-output,
malformed-usage, invalid content type, downstream client cancellation, and streaming
429/500 rejection outcomes with the expected hold/debit behavior. Exact
`text/event-stream` media type validation rejects JSON or lookalike media types
before client output and retains the authorized hold as unknown charge.
The public Provider availability contract and distinct inter-chunk/total timer
contract are now closed for direct and zero-output
resets: Gateway returns `503/provider_unavailable`, dispatch wait exhaustion uses the
same body, and bounded timeout/malformed protocol cases remain `502/provider_protocol_error`.
Final-usage settlement after a truncated stream is now proven through Platform. Actual downstream client socket cancellation
is now proven from an empty stack: the Provider emits one SSE event, a short-lived
client closes before the delayed second write, and the lease remains
  `reconciliation_needed` with its hold and idempotency key retained. A no-header
  Provider timeout is bounded by the first-token deadline and returns a non-empty
  502/provider_protocol_error response; the incoming client socket is extended for
  the configured streaming window.

Deliverables:

- Keep the normalized direct-reset and dispatch-exhaustion `503/provider_unavailable`
  contract stable while adding protocol-wide golden fixtures. The independent
  inter-chunk/total timer tests pass; freeze the retryable/non-retryable mapping
  before extending adapters.
- Propagate client cancellation through the HTTP/SSE transport and stop retrying as
  soon as any response bytes have reached the client. Gateway source behavior,
  stack-level socket/reconciliation evidence, and usage-before-EOF settlement now
  pass; add replay-after-restart assertions for the truncated-stream outbox.
- Cancellation before `forwarded` evidence may expire/abort and release without a
  usage debit. Once Provider transport has been authorized, absence of client output
  does not prove no charge: continue collecting final usage when possible or enter
  `reconciliation_needed`. After `output_started`, retries are always forbidden.
- Extend the isolated fault accounts to SSE: Provider disconnect before first event,
  disconnect after partial output, malformed usage, invalid content type, and established-SSE status
  retention now pass in the empty-stack gate, as do streaming 429/500 no-charge
  rejections. The no-header timeout and separate inter-chunk/total stream timers are
  now covered; usage-before-EOF behavior is covered by the late-usage profile. Add
  protocol-wide assertions for the remaining adapters. Actual client disconnect and
  invalid content type are covered by the current fault matrix.
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
- Keep the revision-3 Cap'n Proto schema greenfield. Contract changes update the
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
- Add metrics and alerts for active/aged holds, reconciliation-needed leases,
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

1. Finish package 1 hook-matrix and recovery tests first; dispatch evidence, the
   authoritative unknown-charge state, audited incident decisions, and one
   pre-commit crash replay now exist. Provider-side streaming cancellation semantics
   and actual client cancellation are source- and empty-stack-tested, but cancellation
   cannot be release-complete without the public error contract, final-usage replay,
   every deterministic boundary, and multi-instance recovery.
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
